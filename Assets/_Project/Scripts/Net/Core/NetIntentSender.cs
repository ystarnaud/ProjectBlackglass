using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// When the client sends steering messages (networking experiment, spec section 3.5): at once when the held direction
    /// changes, then every tenth of a second while it is held (the server drops an intent it has not heard of for 0.5 s), and
    /// exactly one zero when it is released. Pure: the caller passes real time in seconds.
    /// </summary>
    public sealed class NetIntentSender
    {
        public const double RefreshSeconds = 0.1d;

        Vector3 last;
        double lastSent = double.NegativeInfinity;

        /// <summary>True when a steering message with direction `send` must go out now.</summary>
        public bool Next(Vector3 held, double now, out Vector3 send)
        {
            send = held;
            var changed = held != last;
            if (held == Vector3.zero)
            {
                if (!changed)
                    return false;
                Mark(held, now);
                return true;
            }
            if (!changed && now - lastSent < RefreshSeconds)
                return false;
            Mark(held, now);
            return true;
        }

        /// <summary>
        /// A ground-click order is about to be sent: while a non-zero intent is held, a zero must go first, because
        /// CommandableUnit drops an order that is accepted while it is steered (its existing "manual control wins" rule).
        /// </summary>
        public bool ReleaseBeforeOrder(out Vector3 send)
        {
            send = Vector3.zero;
            if (last == Vector3.zero)
                return false;
            last = Vector3.zero;
            return true;
        }

        void Mark(Vector3 direction, double now)
        {
            last = direction;
            lastSent = now;
        }
    }
}
