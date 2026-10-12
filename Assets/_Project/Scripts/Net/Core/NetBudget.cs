using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// One connection's allowance (networking experiment, decision 046): a token bucket that drops requests beyond a steady
    /// rate plus a burst, and a sliding count of dropped or rejected messages that tells the host when a connection is abusive.
    /// Pure: the caller passes real time in seconds.
    /// </summary>
    public sealed class NetBudget
    {
        readonly double perSecond;
        readonly double burst;
        readonly int abuseLimit;
        readonly double abuseWindow;
        readonly Queue<double> refusals = new Queue<double>();
        double tokens;
        double last;
        bool started;

        public NetBudget(double requestsPerSecond, int burst, int abuseLimit, double abuseWindowSeconds)
        {
            perSecond = requestsPerSecond;
            this.burst = burst;
            this.abuseLimit = abuseLimit;
            abuseWindow = abuseWindowSeconds;
            tokens = burst;
        }

        /// <summary>True when this request may proceed; false when the connection is over its rate and the request is dropped.</summary>
        public bool TryTake(double now)
        {
            if (started)
                tokens = Math.Min(burst, tokens + Math.Max(0d, now - last) * perSecond);
            started = true;
            last = now;
            if (tokens < 1d)
                return false;
            tokens -= 1d;
            return true;
        }

        /// <summary>Records one dropped or rejected message. True when more than the limit fall inside the window: disconnect.</summary>
        public bool NoteRefusal(double now)
        {
            refusals.Enqueue(now);
            while (refusals.Count > 0 && now - refusals.Peek() > abuseWindow)
                refusals.Dequeue();
            return refusals.Count > abuseLimit;
        }
    }
}
