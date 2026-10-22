using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// The play client's placeholder HUD (IMGUI):
    /// - connection and map status;
    /// - the known objectives;
    /// - squad cards with health bars;
    /// - health bars over visible units;
    /// - floating combat text;
    /// - the extraction and last-known labels;
    /// - the last refused request or click feedback;
    /// - the controls;
    /// - the end banner.
    /// It shows only what the client was sent. Clicks on its panels never reach the world (NetPlayControls.Blocks).
    /// </summary>
    public sealed class NetPlayHud : MonoBehaviour
    {
        const float FloatSeconds = 1.2f;

        [SerializeField] NetClient client;
        [SerializeField] NetPlayControls controls;
        [SerializeField] NetPlayAvatars avatars;
        [SerializeField] NetMapView mapView;
        [SerializeField] Camera viewCamera;

        readonly List<(int Id, string Text, float Born)> floaters = new List<(int, string, float)>();
        GUIStyle banner;
        GUIStyle label;

        internal void Initialize(NetClient netClient, NetPlayControls playControls, NetPlayAvatars playAvatars, NetMapView map, Camera camera)
        {
            client = netClient;
            controls = playControls;
            avatars = playAvatars;
            mapView = map;
            viewCamera = camera;
        }

        void OnEnable()
        {
            if (avatars != null)
                avatars.Floater += OnFloater;
            if (controls != null)
                controls.Blocks = screen => NetPlayText.Covers(new Vector2(screen.x, Screen.height - screen.y), client != null ? client.OwnedCount : 0, Screen.height);
            // The selection callback is a delegate, which a scene cannot serialize, so it is wired here at runtime.
            if (avatars != null && controls != null)
                avatars.SetSelection(controls.IsSelected);
        }

        void OnDisable()
        {
            if (avatars != null)
                avatars.Floater -= OnFloater;
        }

        void OnFloater(int unitId, string text) => floaters.Add((unitId, text, Time.time));

        void OnGUI()
        {
            if (client == null)
                return;
            if (label == null)
                label = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = false };
            GUI.Label(new Rect(10f, 10f, 900f, 24f), StatusLine(), label);
            DrawObjectives();
            DrawSquad();
            DrawWorldLabels();
            GUI.Label(new Rect(10f, Screen.height - 30f, Screen.width - 20f, 24f), NetPlayText.Help, label);
            var result = NetPlayText.ResultBanner(client.Record.Result);
            if (result != null)
                DrawBanner(result, "The server froze the result. Close the window to finish.");
            else if (client.State == NetClientState.Disconnected || client.State == NetClientState.Refused)
                DrawBanner(client.State == NetClientState.Refused ? "REFUSED (" + client.Refusal + ")" : "DISCONNECTED", "The server is gone; nothing more can be ordered.");
        }

        string StatusLine()
        {
            var text = new StringBuilder("State ").Append(client.State).Append("   round trip ").Append(client.RoundTripMs).Append(" ms");
            var map = NetPlayText.MapStatus(client.Record);
            if (map != null)
                text.Append("   ").Append(map);
            if (controls != null && controls.LastFeedback != null)
                text.Append("   ").Append(controls.LastFeedback);
            else if (client.LastReject != NetRejectReason.None)
                text.Append("   last order refused: ").Append(client.LastReject);
            if (controls != null && controls.Following)
                text.Append("   FOLLOWING (WASD steers, Esc frees the camera)");
            return text.ToString();
        }

        void DrawObjectives()
        {
            GUI.Box(NetPlayText.ObjectivesPanel, "Objectives");
            var y = NetPlayText.ObjectivesPanel.y + 24f;
            foreach (var objective in client.Record.Objectives)
            {
                GUI.Label(new Rect(NetPlayText.ObjectivesPanel.x + 10f, y, NetPlayText.ObjectivesPanel.width - 20f, 22f), NetPlayText.ObjectiveLine(objective), label);
                y += 22f;
            }
        }

        void DrawSquad()
        {
            var panel = NetPlayText.SquadPanel(client.OwnedCount, Screen.height);
            GUI.Box(panel, "Squad");
            var y = panel.y + 26f;
            for (var i = 0; i < client.OwnedCount; i++)
            {
                var id = client.FirstOwnedId + i;
                var state = Find(id);
                var line = "Operative " + (i + 1) + (controls != null && controls.IsSelected(id) ? "  [selected]" : string.Empty);
                if (state != null)
                    line += state.Alive ? "   " + state.Health + "/" + state.MaxHealth + "   " + state.Order : "   DOWN";
                GUI.Label(new Rect(panel.x + 10f, y, 260f, 22f), line, label);
                if (state != null && state.Alive)
                    Bar(new Rect(panel.x + 270f, y + 6f, 130f, 10f), state.Health, state.MaxHealth, new Color(0.2f, 0.8f, 0.3f));
                y += NetPlayText.SquadRowHeight;
            }
        }

        void DrawWorldLabels()
        {
            if (viewCamera == null)
                return;
            foreach (var state in NetClient.VisibleStates())
            {
                if (!state.Alive || !ToGui(state.transform.position + Vector3.up * 2.3f, out var p))
                    continue;
                Bar(new Rect(p.x - 25f, p.y, 50f, 6f), state.Health, state.MaxHealth, state.Hostile ? new Color(0.9f, 0.25f, 0.2f) : new Color(0.2f, 0.8f, 0.3f));
            }
            if (avatars != null)
                foreach (var pair in avatars.LastKnown)
                    if (ToGui(pair.Value + Vector3.up * 0.5f, out var p))
                        GUI.Label(new Rect(p.x - 40f, p.y - 10f, 80f, 20f), "? last seen", label);
            if (mapView != null && mapView.Extraction != null && ToGui(mapView.Extraction.position + Vector3.up * 3.2f, out var zone))
                GUI.Label(new Rect(zone.x - 50f, zone.y - 10f, 100f, 20f), "EXTRACTION", label);
            floaters.RemoveAll(f => Time.time - f.Born > FloatSeconds);
            foreach (var (id, text, born) in floaters)
            {
                var state = Find(id);
                if (state != null && ToGui(state.transform.position + Vector3.up * (2.6f + (Time.time - born)), out var p))
                    GUI.Label(new Rect(p.x - 30f, p.y - 10f, 60f, 20f), text, label);
            }
        }

        void DrawBanner(string title, string detail)
        {
            if (banner == null)
                banner = new GUIStyle(GUI.skin.box) { fontSize = 40, alignment = TextAnchor.MiddleCenter };
            var rect = new Rect(Screen.width * 0.5f - 300f, Screen.height * 0.5f - 70f, 600f, 100f);
            GUI.Box(rect, title, banner);
            GUI.Label(new Rect(rect.x, rect.yMax + 6f, rect.width, 24f), detail, label);
        }

        static void Bar(Rect rect, int value, int max, Color colour)
        {
            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0f, new Color(0f, 0f, 0f, 0.6f), 0f, 0f);
            var fill = rect;
            fill.width = rect.width * (max > 0 ? Mathf.Clamp01((float)value / max) : 0f);
            GUI.DrawTexture(fill, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0f, colour, 0f, 0f);
        }

        // World point to IMGUI coordinates (origin top left); false behind the camera.
        bool ToGui(Vector3 world, out Vector2 gui)
        {
            var p = viewCamera.WorldToScreenPoint(world);
            gui = new Vector2(p.x, Screen.height - p.y);
            return p.z > 0f;
        }

        static NetUnitState Find(int unitId)
        {
            foreach (var state in NetClient.VisibleStates())
                if (state.UnitId == unitId)
                    return state;
            return null;
        }
    }
}
