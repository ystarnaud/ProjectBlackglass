using UnityEngine;

namespace Blackglass
{
    /// <summary>Debug feedback: briefly tints the renderer when its Health takes damage. Runs on scaled time.</summary>
    [RequireComponent(typeof(Health))]
    public sealed class HitFlash : MonoBehaviour
    {
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [SerializeField] Renderer targetRenderer;
        [SerializeField] Color flashColor = Color.white;
        [SerializeField, Min(0f)] float duration = 0.15f;

        Health health;
        MaterialPropertyBlock block;
        float remaining;

        void Awake()
        {
            health = GetComponent<Health>();
            if (targetRenderer == null)
                targetRenderer = GetComponentInChildren<Renderer>();
            block = new MaterialPropertyBlock();
        }

        void OnEnable() => health.Damaged += OnDamaged;

        void OnDisable()
        {
            health.Damaged -= OnDamaged;
            remaining = 0f;
            ClearFlash();
        }

        void Update()
        {
            if (remaining <= 0f || !SimulationTime.IsRunning)
                return;
            remaining -= Time.deltaTime;
            if (remaining <= 0f)
                ClearFlash();
        }

        void OnDamaged(int amount)
        {
            if (targetRenderer == null)
                return;
            remaining = duration;
            targetRenderer.GetPropertyBlock(block);
            block.SetColor(BaseColorId, flashColor);
            targetRenderer.SetPropertyBlock(block);
        }

        void ClearFlash()
        {
            if (targetRenderer != null)
                targetRenderer.SetPropertyBlock(null);
        }
    }
}
