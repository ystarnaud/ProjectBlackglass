using System;
using System.Collections.Generic;

namespace Blackglass
{
    /// <summary>
    /// A unit's orders: the one being carried out plus the ones waiting behind it, in order.
    /// Only records order; CommandableUnit decides when commands start and finish.
    /// </summary>
    public sealed class CommandQueue
    {
        readonly List<UnitCommand> pending = new List<UnitCommand>();

        /// <summary>The order being carried out, or null when idle. Never null while orders are pending.</summary>
        public UnitCommand Current { get; private set; }

        /// <summary>Orders waiting behind the current one, in the order they will run.</summary>
        public IReadOnlyList<UnitCommand> Pending => pending;

        /// <summary>Drops all orders; the command becomes current.</summary>
        public void Replace(UnitCommand command)
        {
            Current = command ?? throw new ArgumentNullException(nameof(command));
            pending.Clear();
        }

        /// <summary>Queues the command last. Returns true if the queue was idle, so it became current.</summary>
        public bool Append(UnitCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            if (Current == null)
            {
                Current = command;
                return true;
            }
            pending.Add(command);
            return false;
        }

        public void Clear()
        {
            Current = null;
            pending.Clear();
        }

        /// <summary>Drops the current order and promotes the first pending one. Returns the new current order, or null.</summary>
        public UnitCommand Advance()
        {
            if (pending.Count == 0)
            {
                Current = null;
                return null;
            }
            Current = pending[0];
            pending.RemoveAt(0);
            return Current;
        }
    }
}
