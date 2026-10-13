using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// What the net-test client shows: a capsule on every replicated avatar (blue for the player's own units, green for other
    /// friendlies, red for hostiles; a grey disc where a hostile was last seen), a top-down camera that frames them, and a text overlay with the connection, the round-trip
    /// time, the last refused request and each own unit's health and orders. The avatars carry no visuals; the capsules are
    /// built here, with a material the scene references so a player build always has its shader.
    /// </summary>
    public sealed class NetClientView : MonoBehaviour
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [SerializeField] NetClient client;
        [SerializeField] Camera viewCamera;
        [SerializeField] Material unitMaterial;

        readonly Dictionary<NetUnitState, GameObject> visuals = new Dictionary<NetUnitState, GameObject>();
        readonly Dictionary<int, GameObject> markers = new Dictionary<int, GameObject>();
        readonly List<int> staleMarkers = new List<int>();
        MaterialPropertyBlock block;

        internal void Initialize(NetClient netClient, Camera camera, Material material)
        {
            client = netClient;
            viewCamera = camera;
            unitMaterial = material;
        }

        void LateUpdate()
        {
            if (client != null)
                DrawLastKnown();
            var states = NetClient.VisibleStates();
            var centre = Vector3.zero;
            var counted = 0;
            var extent = 0f;
            foreach (var state in states)
            {
                if (state.UnitId <= 0)
                    continue;
                if (!visuals.TryGetValue(state, out var visual) || visual == null)
                    visuals[state] = visual = Create(state);
                Style(visual, state);
                centre += state.transform.position;
                counted++;
            }
            foreach (var pair in visuals)
            {
                if (pair.Value != null && pair.Key != null && !pair.Key.IsClientInitialized)
                    pair.Value.SetActive(false);
            }
            if (counted == 0 || viewCamera == null)
                return;
            centre /= counted;
            foreach (var state in states)
            {
                if (state.UnitId > 0)
                    extent = Mathf.Max(extent, (state.transform.position - centre).magnitude);
            }
            viewCamera.orthographic = true;
            viewCamera.orthographicSize = Mathf.Max(8f, extent + 4f);
            viewCamera.transform.SetPositionAndRotation(centre + Vector3.up * 40f, Quaternion.Euler(90f, 0f, 0f));
        }

        GameObject Create(NetUnitState state)
        {
            var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.name = "Unit_" + state.UnitId;
            capsule.transform.SetParent(state.transform, false);
            capsule.transform.localPosition = new Vector3(0f, 1f, 0f);
            capsule.GetComponent<Renderer>().sharedMaterial = unitMaterial;
            return capsule;
        }

        void Style(GameObject visual, NetUnitState state)
        {
            visual.SetActive(state.Alive);
            var owned = state.UnitId >= client.FirstOwnedId && state.UnitId < client.FirstOwnedId + client.OwnedCount;
            var color = state.Hostile ? new Color(0.85f, 0.2f, 0.2f) : owned ? new Color(0.2f, 0.45f, 0.95f) : new Color(0.25f, 0.75f, 0.3f);
            if (block == null)
                block = new MaterialPropertyBlock();
            block.SetColor(BaseColor, color);
            visual.GetComponent<Renderer>().SetPropertyBlock(block);
        }

        // A last-known record is a flat grey disc where the hostile was last seen; it never moves until the record changes.
        void DrawLastKnown()
        {
            foreach (var pair in client.Record.LastKnown)
            {
                if (!markers.TryGetValue(pair.Key, out var marker) || marker == null)
                    markers[pair.Key] = marker = CreateMarker(pair.Key);
                marker.transform.position = pair.Value + new Vector3(0f, 0.05f, 0f);
            }
            staleMarkers.Clear();
            foreach (var pair in markers)
            {
                if (!client.Record.LastKnown.ContainsKey(pair.Key))
                    staleMarkers.Add(pair.Key);
            }
            foreach (var id in staleMarkers)
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
            Destroy(disc.GetComponent<Collider>());
            disc.transform.localScale = new Vector3(1.2f, 0.02f, 1.2f);
            var renderer = disc.GetComponent<Renderer>();
            renderer.sharedMaterial = unitMaterial;
            if (block == null)
                block = new MaterialPropertyBlock();
            block.SetColor(BaseColor, new Color(0.55f, 0.55f, 0.55f));
            renderer.SetPropertyBlock(block);
            return disc;
        }

        void OnGUI()
        {
            if (client == null)
                return;
            var text = new StringBuilder();
            text.Append("state ").Append(client.State);
            if (client.State == NetClientState.Refused)
                text.Append(" (").Append(client.Refusal).Append(')');
            text.Append("   round trip ").Append(client.RoundTripMs).Append(" ms");
            if (client.LastReject != NetRejectReason.None)
                text.Append("\nlast refused: ").Append(client.LastReject).Append(" (command ").Append(client.LastRejectCommand).Append(')');
            text.Append("\nlast known: ").Append(client.Record.LastKnown.Count);
            foreach (var state in NetClient.VisibleStates())
            {
                if (state.UnitId < client.FirstOwnedId || state.UnitId >= client.FirstOwnedId + client.OwnedCount)
                    continue;
                text.Append("\n#").Append(state.UnitId).Append("  hp ").Append(state.Health).Append('/').Append(state.MaxHealth)
                    .Append("  ").Append(state.Order).Append("  queue ").Append(state.Queue);
            }
            text.Append("\nWASD steer   click move (Shift queues)   click a red unit attack   X stop   1 2 pick unit");
            GUI.Label(new Rect(10f, 10f, 900f, 200f), text.ToString());
        }
    }
}
