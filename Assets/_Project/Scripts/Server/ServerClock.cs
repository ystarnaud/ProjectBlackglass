using System;
using System.Diagnostics;

namespace Blackglass
{
    /// <summary>
    /// The attempt's wall clock for the manifest's serverElapsedMs (Stage 12): elapsed real time since it was started, unaffected
    /// by the tactical pause or the time scale. Stage 12 starts it when the attempt session begins; Stage 13 at the start commit.
    /// </summary>
    public sealed class ServerClock
    {
        readonly Func<long> nowMs;
        long startMs;

        public ServerClock() : this(StopwatchMs) { }

        /// <summary>Tests pass their own millisecond source.</summary>
        internal ServerClock(Func<long> millisecondSource)
        {
            nowMs = millisecondSource ?? throw new ArgumentNullException(nameof(millisecondSource));
            startMs = nowMs();
        }

        static readonly Stopwatch Watch = Stopwatch.StartNew();
        static long StopwatchMs() => Watch.ElapsedMilliseconds;

        public void Restart() => startMs = nowMs();

        /// <summary>Whole milliseconds since the start; never negative, capped at int.MaxValue (the manifest's own bound refuses more).</summary>
        public int ElapsedMs => (int)Math.Min(int.MaxValue, Math.Max(0L, nowMs() - startMs));
    }
}
