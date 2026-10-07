using System.Linq;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>World-space measurements of a skinned character as it is posed right now.</summary>
    internal static class DariusMeasure
    {
        /// <summary>
        /// The bounds of the body mesh as currently deformed. SkinnedMeshRenderer.bounds is the padded rest-pose box,
        /// useless for feet-on-the-ground checks, so the mesh is baked (with the transform scale, which the visual carries, applied once).
        /// </summary>
        public static Bounds PosedBodyBounds(GameObject visual)
        {
            var body = visual.GetComponentInChildren<SkinnedMeshRenderer>();
            var baked = new Mesh();
            body.BakeMesh(baked, true);
            var points = baked.vertices.Select(v => body.transform.TransformPoint(v)).ToArray();
            Object.Destroy(baked);
            var bounds = new Bounds(points[0], Vector3.zero);
            foreach (var p in points)
                bounds.Encapsulate(p);
            return bounds;
        }

        /// <summary>True when the animator is in the named state, or blending into it.</summary>
        public static bool InState(Animator animator, string state)
        {
            if (animator.GetCurrentAnimatorStateInfo(0).IsName(state))
                return true;
            return animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).IsName(state);
        }
    }
}
