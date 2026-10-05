using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Debug feedback: shows a line from this unit to its target for a moment after each shot, hit or miss. The LineRenderer lives
    /// on a collider-free child, because a friendly unit's own LineRenderer belongs to CommandQueueView. Counts down
    /// on simulation time, so it freezes while paused.
    /// </summary>
    [RequireComponent(typeof(UnitAttacker))]
    public sealed class AttackLineView : MonoBehaviour
    {
        [SerializeField] LineRenderer line;
        [SerializeField, Min(0f)] float duration = 0.15f;
        // Height above the unit pivots (the capsule centre) where the line is drawn.
        [SerializeField] float height = 0.5f;

        UnitAttacker attacker;
        float remaining;

        internal bool IsShowing => line != null && line.enabled;

        internal void Initialize(LineRenderer lineRenderer) => line = lineRenderer;

        void Awake() => attacker = GetComponent<UnitAttacker>();

        void OnEnable()
        {
            attacker.Attacked += OnAttacked;
            attacker.Missed += OnAttacked;
            Hide();
        }

        void OnDisable()
        {
            attacker.Attacked -= OnAttacked;
            attacker.Missed -= OnAttacked;
            remaining = 0f;
            Hide();
        }

        void Update()
        {
            if (remaining <= 0f || !SimulationTime.IsRunning)
                return;
            remaining -= Time.deltaTime;
            if (remaining <= 0f)
                Hide();
        }

        void OnAttacked(Health target)
        {
            if (line == null || target == null)
                return;
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.SetPosition(0, transform.position + Vector3.up * height);
            line.SetPosition(1, target.transform.position + Vector3.up * height);
            line.enabled = true;
            remaining = duration;
        }

        void Hide()
        {
            if (line != null)
                line.enabled = false;
        }
    }
}
