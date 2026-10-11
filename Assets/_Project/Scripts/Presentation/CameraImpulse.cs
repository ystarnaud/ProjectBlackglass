using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A decaying camera wobble source. Lives on the camera rig; TacticalCameraController adds LocalOffset to the viewing
    /// camera's local position. It advances on scaled time only, so a tactical pause holds it still while the camera's own
    /// pan, zoom and rotation (unscaled) keep working. Strength 0 does nothing.
    /// </summary>
    public sealed class CameraImpulse : MonoBehaviour
    {
        // Impulses fade out beyond this ground distance from the rig.
        public const float FalloffDistance = 40f;

        readonly ImpulseEnvelope envelope = new ImpulseEnvelope();
        readonly CosmeticRandom random = new CosmeticRandom();

        /// <summary>The offset in the camera's local space (x right, y up).</summary>
        public Vector3 LocalOffset
        {
            get
            {
                var o = envelope.Offset;
                return new Vector3(o.x, o.y, 0f);
            }
        }

        public void Add(float strength) => envelope.Add(strength, random.Range(0f, 6.28f), random.Range(0f, 6.28f));

        /// <summary>1 at the rig, falling to 0 at FalloffDistance (flat distance).</summary>
        public float DistanceFactor(Vector3 world)
        {
            var d = world - transform.position;
            d.y = 0f;
            return Mathf.Clamp01(1f - d.magnitude / FalloffDistance);
        }

        public void Clear() => envelope.Reset();

        void Update()
        {
            if (SimulationTime.IsRunning)
                envelope.Advance(Time.deltaTime);
        }

        void OnDisable() => envelope.Reset();
    }
}
