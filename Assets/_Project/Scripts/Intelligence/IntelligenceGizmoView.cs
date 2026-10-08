using UnityEngine;

namespace Blackglass
{
    /// <summary>Debug-only gizmos for the truth view: region outlines coloured by state, camera cones and running scans.</summary>
    public sealed class IntelligenceGizmoView : MonoBehaviour
    {
        [SerializeField] IntelligenceService intelligence;

        void OnDrawGizmos()
        {
            if (intelligence == null || !intelligence.TruthView || intelligence.Map == null)
                return;
            var map = intelligence.Map;
            for (var region = 0; region < map.Count; region++)
            {
                var state = intelligence.StateOfRegion(region);
                Gizmos.color = state == KnowledgeState.Observed ? Color.white : state == KnowledgeState.Discovered ? Color.gray : new Color(0.6f, 0.2f, 0.2f);
                var rect = map.WorldRect(region);
                Gizmos.DrawWireCube(new Vector3(rect.center.x, 0.1f, rect.center.y), new Vector3(rect.width, 0.1f, rect.height));
            }
            var network = intelligence.Network;
            if (network != null)
            {
                foreach (var camera in network.Cameras)
                {
                    Gizmos.color = network.Compromised ? Color.green : Color.yellow;
                    var left = Quaternion.Euler(0f, -camera.HalfAngle, 0f) * camera.Forward * camera.Range;
                    var right = Quaternion.Euler(0f, camera.HalfAngle, 0f) * camera.Forward * camera.Range;
                    Gizmos.DrawLine(camera.Position, camera.Position + left);
                    Gizmos.DrawLine(camera.Position, camera.Position + right);
                    Gizmos.DrawLine(camera.Position + left, camera.Position + right);
                }
            }
            Gizmos.color = Color.cyan;
            foreach (var pulse in intelligence.Pulses)
                Gizmos.DrawWireSphere(pulse.Centre, pulse.Radius);
        }
    }
}
