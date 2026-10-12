using FishNet.Object;
using FishNet.Object.Synchronizing;

namespace Blackglass
{
    /// <summary>
    /// The replicated state of one unit (networking experiment, spec section 3.4): who it is, its side, its health and what it
    /// is doing. Lives on the avatar object the server spawns for each unit; only the server writes it. Position and rotation
    /// travel on the avatar's NetworkTransform. A client reads these properties; no message can change them.
    /// </summary>
    public sealed class NetUnitState : NetworkBehaviour
    {
        readonly SyncVar<int> unitId = new SyncVar<int>();
        readonly SyncVar<bool> hostile = new SyncVar<bool>();
        readonly SyncVar<int> health = new SyncVar<int>();
        readonly SyncVar<int> maxHealth = new SyncVar<int>();
        readonly SyncVar<bool> alive = new SyncVar<bool>();
        readonly SyncVar<byte> order = new SyncVar<byte>();
        readonly SyncVar<int> queue = new SyncVar<int>();
        readonly SyncVar<bool> steering = new SyncVar<bool>();

        public int UnitId => unitId.Value;
        public bool Hostile => hostile.Value;
        public int Health => health.Value;
        public int MaxHealth => maxHealth.Value;
        public bool Alive => alive.Value;
        public NetOrderKind Order => (NetOrderKind)order.Value;
        /// <summary>Orders waiting behind the current one.</summary>
        public int Queue => queue.Value;
        public bool Steering => steering.Value;

        // Server only, after the avatar has been spawned.
        internal void Assign(int id, bool isHostile)
        {
            unitId.Value = id;
            hostile.Value = isHostile;
        }

        internal void Push(int currentHealth, int maximumHealth, bool isAlive, NetOrderKind kind, int queued, bool isSteering)
        {
            health.Value = currentHealth;
            maxHealth.Value = maximumHealth;
            alive.Value = isAlive;
            order.Value = (byte)kind;
            queue.Value = queued;
            steering.Value = isSteering;
        }
    }
}
