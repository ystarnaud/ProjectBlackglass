using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Presentation only: tints the unit's skinned body with a team colour so units that share one model can be told apart
    /// (the weapon and the placeholder capsule are left alone). The colour is multiplied into the material's base map through
    /// a property block, so every team shares one material and nothing in gameplay reads it. A future team system only has to
    /// call <see cref="SetTeamColor"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UnitTeamTint : MonoBehaviour
    {
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [SerializeField] Color teamColor = Color.white;

        MaterialPropertyBlock block;

        public Color TeamColor => teamColor;

        public void SetTeamColor(Color color)
        {
            teamColor = color;
            Apply();
        }

        /// <summary>Writes the colour onto every skinned renderer below this unit. Safe to call repeatedly.</summary>
        public void Apply()
        {
            block ??= new MaterialPropertyBlock();
            foreach (var body in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                body.GetPropertyBlock(block);
                block.SetColor(BaseColorId, teamColor);
                body.SetPropertyBlock(block);
            }
        }

        void OnEnable() => Apply();
    }
}
