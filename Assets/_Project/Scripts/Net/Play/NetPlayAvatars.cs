using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The play client's units:
    /// - an NetAvatarView on every avatar this client was sent;
    /// - a red disc where a hostile was last seen (the server's frozen record; it never moves);
    /// - a tracer for every relayed shot;
    /// - floating-text events for the HUD ("-N" on a health drop, "MISS" on a shot cover turned away).
    /// Reads only replicated state and the messages; decides nothing.
    /// </summary>
    public sealed class NetPlayAvatars : MonoBehaviour
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [SerializeField] NetClient client;
        [SerializeField] GameObject visualPrefab;
        [SerializeField] WeaponVisualAsset weapon;
        [SerializeField] GameObject tracerPrefab;
        [SerializeField] Material ringMaterial;
        [SerializeField] Material markerMaterial;

        readonly Dictionary<NetUnitState, NetAvatarView> views = new Dictionary<NetUnitState, NetAvatarView>();
        readonly Dictionary<int, NetAvatarView> byId = new Dictionary<int, NetAvatarView>();
        readonly Dictionary<int, int> lastHealth = new Dictionary<int, int>();
        readonly Dictionary<int, GameObject> markers = new Dictionary<int, GameObject>();
        readonly List<int> stale = new List<int>();
        Func<int, bool> isSelected = _ => false;
        TracerStyle tracerStyle;
        NetClient subscribed;

        /// <summary>A unit id and a short text to float above it (the HUD draws it).</summary>
        public event Action<int, string> Floater;

        internal void Initialize(NetClient netClient, Func<int, bool> selected, GameObject visual, WeaponVisualAsset weaponAsset,
            GameObject tracer, Material ring, Material marker)
        {
            client = netClient;
            isSelected = selected ?? (_ => false);
            visualPrefab = visual;
            weapon = weaponAsset;
            tracerPrefab = tracer;
            ringMaterial = ring;
            markerMaterial = marker;
            Subscribe();
        }

        internal void SetSelection(Func<int, bool> selected) => isSelected = selected ?? (_ => false);

        void OnEnable() => Subscribe();

        // The last-known discs live at the scene root; they go with this component (final review M6).
        void OnDestroy()
        {
            foreach (var marker in markers.Values)
                if (marker != null)
                    Destroy(marker);
            markers.Clear();
        }

        void OnDisable()
        {
            if (subscribed != null)
                subscribed.ShotReceived -= OnShot;
            subscribed = null;
        }

        void Subscribe()
        {
            if (client == null || subscribed == client || !isActiveAndEnabled)
                return;
            client.ShotReceived += OnShot;
            subscribed = client;
        }

        public bool IsOwned(NetUnitState state) =>
            client != null && !state.Hostile && state.UnitId >= client.FirstOwnedId && state.UnitId < client.FirstOwnedId + client.OwnedCount;

        public NetAvatarView ViewOf(int unitId) => byId.TryGetValue(unitId, out var view) && view != null && view.isActiveAndEnabled ? view : null;

        void LateUpdate()
        {
            if (client == null)
                return;
            foreach (var state in NetClient.VisibleStates())
            {
                if (!views.TryGetValue(state, out var view) || view == null)
                    views[state] = view = NetAvatarView.Create(state, visualPrefab, weapon, ringMaterial);
                view.gameObject.SetActive(true);
                byId[state.UnitId] = view;
                view.Tick(state, IsOwned(state), isSelected(state.UnitId), Time.deltaTime);
                if (lastHealth.TryGetValue(state.UnitId, out var before) && state.Health < before)
                {
                    view.Hit();
                    Floater?.Invoke(state.UnitId, "-" + (before - state.Health));
                }
                lastHealth[state.UnitId] = state.Health;
            }
            // Host mode keeps the server's avatars in the scene; one this client is no longer sent must not stay drawn.
            foreach (var pair in views)
                if (pair.Key != null && pair.Value != null && !pair.Key.IsClientInitialized)
                    pair.Value.gameObject.SetActive(false);
            DrawLastKnown();
        }

        // A shot naming an avatar this client does not hold draws nothing (checkpoint 1 review M11: the client does not trust the ids).
        internal void OnShot(NetShot shot)
        {
            var attacker = ViewOf(shot.AttackerId);
            var target = ViewOf(shot.TargetId);
            if (attacker == null || target == null)
                return;
            attacker.Fire();
            if (!shot.Hit)
                Floater?.Invoke(shot.TargetId, "MISS");
            if (tracerPrefab == null)
                return;
            var tracer = Instantiate(tracerPrefab);
            var line = tracer.GetComponent<TracerView>();
            if (line == null)
            {
                Destroy(tracer);
                return;
            }
            if (tracerStyle == null)
                tracerStyle = TracerStyle.Create(tracerPrefab);
            Destroy(tracer, line.Launch(attacker.MuzzlePoint, target.ChestPoint, tracerStyle) + 0.1f);
        }

        void DrawLastKnown()
        {
            foreach (var pair in client.Record.LastKnown)
            {
                if (!markers.TryGetValue(pair.Key, out var marker) || marker == null)
                    markers[pair.Key] = marker = CreateMarker(pair.Key);
                marker.transform.position = pair.Value + new Vector3(0f, 0.05f, 0f);
            }
            stale.Clear();
            foreach (var pair in markers)
                if (!client.Record.LastKnown.ContainsKey(pair.Key))
                    stale.Add(pair.Key);
            foreach (var id in stale)
            {
                if (markers[id] != null)
                    Destroy(markers[id]);
                markers.Remove(id);
            }
        }

        GameObject CreateMarker(int unitId)
        {
            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "LastKnown_" + unitId;
            DestroyImmediate(disc.GetComponent<Collider>());
            disc.transform.localScale = new Vector3(1.2f, 0.02f, 1.2f);
            var renderer = disc.GetComponent<Renderer>();
            if (markerMaterial != null)
                renderer.sharedMaterial = markerMaterial;
            var block = new MaterialPropertyBlock();
            block.SetColor(BaseColor, new Color(0.9f, 0.25f, 0.25f, 0.5f));
            renderer.SetPropertyBlock(block);
            return disc;
        }

        /// <summary>Where the HUD draws a last-known "?" label: the frozen records, as given.</summary>
        public IReadOnlyDictionary<int, Vector3> LastKnown => client != null ? client.Record.LastKnown : new Dictionary<int, Vector3>();
    }
}
