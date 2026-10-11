using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// A hitscan streak: a short bright segment that travels from the muzzle to a fixed end point at a configured speed
    /// (or is drawn at once). Purely cosmetic: it carries no collider and nothing waits for it. Advances only while the
    /// simulation runs, so a tactical pause freezes it where it is.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public sealed class TracerView : MonoBehaviour
    {
        const float ImmediateSeconds = 0.05f;

        LineRenderer line;
        Vector3 start;
        Vector3 direction;
        float total;
        float length;
        float speed;
        float head;

        LineRenderer Line => line != null ? line : line = GetComponent<LineRenderer>();

        public bool IsRunning { get; private set; }
        public Vector3 HeadPoint => start + direction * Mathf.Min(head, total);
        public Vector3 TailPoint => start + direction * Mathf.Clamp(head - length, 0f, total);

        /// <summary>
        /// Starts a streak between two captured points. Returns the lifetime the owner should give the effect. A zero-length or
        /// non-finite segment draws nothing (the detail is omitted rather than invented).
        /// </summary>
        public float Launch(Vector3 from, Vector3 to, TracerStyle style)
        {
            var delta = to - from;
            total = delta.magnitude;
            if (!(total > 0.01f) || float.IsInfinity(total))
            {
                ResetState();
                return 0.01f;
            }
            start = from;
            direction = delta / total;
            length = Mathf.Max(0.1f, style.Length);
            speed = style.Speed > 0f ? style.Speed : total / ImmediateSeconds;
            head = Mathf.Min(0.5f, total);
            var l = Line;
            l.useWorldSpace = true;
            l.positionCount = 2;
            l.widthMultiplier = style.Width;
            l.startColor = style.StartColor;
            l.endColor = style.EndColor;
            IsRunning = true;
            Apply();
            return (total + length) / speed + 0.05f;
        }

        void Update()
        {
            if (!IsRunning || !SimulationTime.IsRunning)
                return;
            head += speed * Time.deltaTime;
            Apply();
        }

        void Apply()
        {
            var tail = head - length;
            if (tail >= total)
            {
                Line.enabled = false;
                IsRunning = false;
                return;
            }
            var a = Mathf.Max(0f, tail);
            var b = Mathf.Min(head, total);
            Line.enabled = b > a;
            Line.SetPosition(0, start + direction * a);
            Line.SetPosition(1, start + direction * b);
        }

        public void ResetState()
        {
            IsRunning = false;
            head = 0f;
            total = 0f;
            Line.enabled = false;
        }
    }
}
