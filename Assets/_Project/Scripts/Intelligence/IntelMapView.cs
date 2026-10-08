using System.Collections.Generic;
using UnityEngine;

namespace Blackglass
{
    /// <summary>Maps the mission's world rectangle into a screen panel (IMGUI coordinates: y down, so world z points up the panel).</summary>
    public static class IntelMapProjection
    {
        const float Padding = 6f;

        /// <summary>The union of every region's world rectangle (x and y of the Rect are world x and z).</summary>
        public static Rect WorldBounds(RegionMap map)
        {
            var bounds = map.WorldRect(0);
            for (var i = 1; i < map.Count; i++)
            {
                var r = map.WorldRect(i);
                bounds = Rect.MinMaxRect(Mathf.Min(bounds.xMin, r.xMin), Mathf.Min(bounds.yMin, r.yMin),
                    Mathf.Max(bounds.xMax, r.xMax), Mathf.Max(bounds.yMax, r.yMax));
            }
            return bounds;
        }

        public static Vector2 ToPanel(Rect panel, Rect bounds, Vector3 world)
        {
            var scale = Scale(panel, bounds);
            var origin = Origin(panel, bounds, scale);
            return new Vector2(origin.x + (world.x - bounds.xMin) * scale, origin.y + (bounds.yMax - world.z) * scale);
        }

        public static Rect ToPanel(Rect panel, Rect bounds, Rect worldRect)
        {
            var scale = Scale(panel, bounds);
            var origin = Origin(panel, bounds, scale);
            return new Rect(origin.x + (worldRect.xMin - bounds.xMin) * scale, origin.y + (bounds.yMax - worldRect.yMax) * scale,
                worldRect.width * scale, worldRect.height * scale);
        }

        static float Scale(Rect panel, Rect bounds)
        {
            var width = Mathf.Max(bounds.width, 0.01f);
            var height = Mathf.Max(bounds.height, 0.01f);
            return Mathf.Max(0.01f, Mathf.Min((panel.width - 2f * Padding) / width, (panel.height - 2f * Padding) / height));
        }

        static Vector2 Origin(Rect panel, Rect bounds, float scale) =>
            new Vector2(panel.x + (panel.width - bounds.width * scale) * 0.5f, panel.y + (panel.height - bounds.height * scale) * 0.5f);
    }

    public enum MapMarkKind
    {
        Region,
        Friendly,
        Enemy,
        LastKnown,
        Device,
        Objective,
    }

    /// <summary>One thing the overlay draws.</summary>
    public readonly struct MapMark
    {
        public MapMark(MapMarkKind kind, Rect rect, Color color, string label = "")
        {
            Kind = kind;
            Rect = rect;
            Color = color;
            Label = label;
        }

        public MapMarkKind Kind { get; }
        public Rect Rect { get; }
        public Color Color { get; }
        public string Label { get; }
    }

    /// <summary>
    /// The prototype tactical map (IMGUI, bottom right, works while paused): the regions coloured by what the player knows
    /// (unknown dark, discovered grey, observed light), the squad, hostiles that are in sight, a marker where a hostile was
    /// last seen, discovered security devices and known objectives. Shown only while fog is on; the developer truth view
    /// also draws the unknown regions and every hostile. Not a production minimap. The marks are built by BuildMarks so tests
    /// can read them.
    /// </summary>
    public sealed class IntelMapView : MonoBehaviour
    {
        internal static readonly Color UnknownColor = new Color(0.07f, 0.07f, 0.09f);
        internal static readonly Color DiscoveredColor = new Color(0.36f, 0.38f, 0.42f);
        internal static readonly Color ObservedColor = new Color(0.78f, 0.82f, 0.88f);
        internal static readonly Color TruthUnknownColor = new Color(0.30f, 0.12f, 0.12f);
        static readonly Color FriendlyColor = new Color(0.25f, 0.9f, 0.35f);
        static readonly Color EnemyColor = new Color(0.95f, 0.2f, 0.2f);
        static readonly Color TruthEnemyColor = new Color(0.5f, 0.1f, 0.1f);
        static readonly Color LastKnownColor = new Color(1f, 0.6f, 0.1f);
        static readonly Color DeviceColor = new Color(0.2f, 0.85f, 0.95f);
        static readonly Color ObjectiveColor = new Color(0.98f, 0.9f, 0.2f);

        [SerializeField] IntelligenceService intelligence;
        [SerializeField] Encounter encounter;
        // Optional: gives the known objectives.
        [SerializeField] MissionDirector director;
        [SerializeField] Vector2 size = new Vector2(260f, 190f);

        readonly List<MapMark> marks = new List<MapMark>();
        bool visible = true;
        GUIStyle labelStyle;

        public bool IsVisible => visible;

        public void Toggle() => visible = !visible;

        internal void Initialize(IntelligenceService service, Encounter currentEncounter, MissionDirector missionDirector)
        {
            intelligence = service;
            encounter = currentEncounter;
            director = missionDirector;
        }

        internal IReadOnlyList<MapMark> BuildMarks(Rect panel)
        {
            marks.Clear();
            if (intelligence == null || !intelligence.IsFogActive || intelligence.Map == null)
                return marks;
            var map = intelligence.Map;
            var truth = intelligence.TruthView;
            var bounds = IntelMapProjection.WorldBounds(map);

            for (var region = 0; region < map.Count; region++)
            {
                var state = intelligence.StateOfRegion(region);
                var color = state == KnowledgeState.Observed ? ObservedColor
                    : state == KnowledgeState.Discovered ? DiscoveredColor
                    : truth ? TruthUnknownColor : UnknownColor;
                marks.Add(new MapMark(MapMarkKind.Region, IntelMapProjection.ToPanel(panel, bounds, map.WorldRect(region)), color));
            }
            foreach (var device in intelligence.Devices)
            {
                if (intelligence.IsDeviceShown(device.Id))
                    marks.Add(Dot(MapMarkKind.Device, panel, bounds, device.Position, DeviceColor, 5f, device.Id == SecurityPlan.TerminalDeviceId ? "T" : "C"));
            }
            if (director != null && director.Runtime != null)
            {
                foreach (var goal in director.Runtime.Objectives)
                {
                    if (goal.IsKnown && goal.HasTarget && goal.State != ObjectiveState.Completed)
                        marks.Add(Dot(MapMarkKind.Objective, panel, bounds, goal.TargetPosition, ObjectiveColor, 7f, "!"));
                }
            }
            if (encounter != null)
            {
                foreach (var hostile in encounter.Hostiles)
                {
                    if (hostile == null || !hostile.IsAlive)
                        continue;
                    var state = intelligence.StateOfEnemy(hostile);
                    if (state == KnowledgeState.Observed)
                        marks.Add(Dot(MapMarkKind.Enemy, panel, bounds, hostile.transform.position, EnemyColor, 6f));
                    else if (state == KnowledgeState.Discovered && intelligence.TryLastKnown(hostile, out var seen))
                        marks.Add(Dot(MapMarkKind.LastKnown, panel, bounds, seen, LastKnownColor, 6f, "?"));
                    else if (truth)
                        marks.Add(Dot(MapMarkKind.Enemy, panel, bounds, hostile.transform.position, TruthEnemyColor, 6f, "x"));
                }
                foreach (var friendly in encounter.Friendlies)
                {
                    if (friendly != null && friendly.IsAlive)
                        marks.Add(Dot(MapMarkKind.Friendly, panel, bounds, friendly.transform.position, FriendlyColor, 6f));
                }
            }
            return marks;
        }

        static MapMark Dot(MapMarkKind kind, Rect panel, Rect bounds, Vector3 world, Color color, float size, string label = "")
        {
            var at = IntelMapProjection.ToPanel(panel, bounds, world);
            return new MapMark(kind, new Rect(at.x - size * 0.5f, at.y - size * 0.5f, size, size), color, label);
        }

        void OnGUI()
        {
            if (!visible || intelligence == null || !intelligence.IsFogActive)
                return;
            var panel = new Rect(Screen.width - size.x - 10f, Screen.height - size.y - 10f, size.x, size.y);
            labelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleCenter };
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            foreach (var mark in BuildMarks(panel))
            {
                GUI.color = mark.Color;
                GUI.DrawTexture(mark.Rect, Texture2D.whiteTexture);
                if (mark.Label.Length > 0)
                {
                    GUI.color = Color.white;
                    GUI.Label(new Rect(mark.Rect.x - 6f, mark.Rect.y - 8f, mark.Rect.width + 12f, 16f), mark.Label, labelStyle);
                }
            }
            GUI.color = Color.white;
            GUI.Label(new Rect(panel.x + 4f, panel.y + 2f, 200f, 18f), intelligence.TruthView ? "INTEL (truth view)" : "INTEL");
        }
    }
}
