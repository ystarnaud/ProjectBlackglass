using System;

namespace Blackglass.Backend.Core
{
    /// <summary>
    /// The manifest's outcomeSequence (owner ruling, Stage 12 Track L final rulings §1): the sequence number of the first
    /// committed terminal gameplay event of the attempt. The counter starts at zero and advances once per committed gameplay
    /// event or state transition (the caller commits only those; refused, malformed, duplicate, unauthorized and no-effect
    /// requests never reach it). The terminal event takes the next number, and the freeze keeps it: nothing afterwards changes
    /// it. An overflow fails closed: once the terminal event could no longer be numbered within <see cref="Maximum"/> (the
    /// manifest's bound), the sequence never wraps or clamps and the freeze fails. Separate from any connection anti-replay
    /// sequence, which may advance on refused requests. Not thread-safe: the simulation thread is its only user.
    /// </summary>
    public sealed class OutcomeSequence
    {
        public OutcomeSequence(int maximum = int.MaxValue)
        {
            if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum), maximum, "The bound is at least 1.");
            Maximum = maximum;
        }

        public int Maximum { get; }
        /// <summary>Committed gameplay events so far (up to the freeze).</summary>
        public int Committed { get; private set; }
        /// <summary>The terminal event's number once frozen successfully, else 0.</summary>
        public int Terminal { get; private set; }
        public bool IsFrozen { get; private set; }
        /// <summary>True once the terminal event could no longer be numbered: sticky, and the freeze then fails.</summary>
        public bool Overflowed { get; private set; }

        /// <summary>One committed gameplay event. Ignored after the freeze or an overflow.</summary>
        public void Commit()
        {
            if (IsFrozen || Overflowed) return;
            Committed++;
            if (Committed >= Maximum) Overflowed = true; // the terminal event would be Maximum + 1
        }

        /// <summary>
        /// Freezes at the terminal event: true with its number (Committed + 1), or false after an overflow (fail closed). A
        /// repeated call answers the same.
        /// </summary>
        public bool TryFreeze(out int terminal)
        {
            if (!IsFrozen)
            {
                IsFrozen = true;
                if (!Overflowed) Terminal = Committed + 1;
            }
            terminal = Terminal;
            return !Overflowed;
        }
    }
}
