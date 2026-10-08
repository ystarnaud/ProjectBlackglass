using UnityEngine;

namespace Blackglass
{
    /// <summary>Debug drawing of the mission structure: room rectangles, the room graph, spawn regions and bounds. Gizmos only.</summary>
    public sealed class MissionGizmoView : MonoBehaviour
    {
        [SerializeField] MissionDirector director;
        [SerializeField] bool draw = true;
        // Optional: with fog on, the room, spawn and guard gizmos stay hidden.
        [SerializeField] IntelligenceService intelligence;

        internal void SetIntelligence(IntelligenceService service) => intelligence = service;

        void OnDrawGizmos()
        {
            if (!draw || director == null || director.Current == null || (intelligence != null && intelligence.IsFogActive && !intelligence.TruthView))
                return;
            var layout = director.Current.Layout;
            Gizmos.color = new Color(0.6f, 0.6f, 0.6f);
            foreach (var room in layout.Rooms)
                Rect(layout, room.Rect, 0.05f);
            Gizmos.color = Color.yellow;
            foreach (var connection in layout.Connections)
                Gizmos.DrawLine(layout.RectCenter(layout.Rooms[connection.RoomA].Rect) + Vector3.up * 0.3f,
                    layout.RectCenter(layout.Rooms[connection.RoomB].Rect) + Vector3.up * 0.3f);
            Gizmos.color = Color.green;
            Rect(layout, layout.FriendlyRegion, 0.1f);
            Gizmos.color = Color.red;
            foreach (var region in layout.HostileRegions)
                Rect(layout, region, 0.1f);
            Gizmos.color = Color.cyan;
            var bounds = layout.WorldBounds;
            Gizmos.DrawWireCube(bounds.center, bounds.size);
        }

        static void Rect(MissionLayout layout, RectInt rect, float height)
        {
            var a = layout.ToWorld(rect.xMin, rect.yMin) + Vector3.up * height;
            var b = layout.ToWorld(rect.xMax, rect.yMin) + Vector3.up * height;
            var c = layout.ToWorld(rect.xMax, rect.yMax) + Vector3.up * height;
            var d = layout.ToWorld(rect.xMin, rect.yMax) + Vector3.up * height;
            Gizmos.DrawLine(a, b);
            Gizmos.DrawLine(b, c);
            Gizmos.DrawLine(c, d);
            Gizmos.DrawLine(d, a);
        }
    }
}
