using UnityEngine;

namespace Blackglass.AssetPipeline
{
    public static class ModelMeasure
    {
        /// <summary>
        /// World-space bounds of all geometry under the instance as currently posed, or null when there is none.
        /// SkinnedMeshRenderer.bounds is a padded rest-pose box, so skinned meshes are baked and their vertices transformed by
        /// the renderer (the same method as the Darius tests). Measure an instance at the origin with identity scale.
        /// </summary>
        public static Bounds? Measure(GameObject instance)
        {
            Bounds? total = null;
            foreach (var skinned in instance.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (skinned.sharedMesh == null) continue;
                var baked = new Mesh();
                skinned.BakeMesh(baked, true);
                foreach (var vertex in baked.vertices)
                    Grow(ref total, skinned.transform.TransformPoint(vertex));
                Object.DestroyImmediate(baked);
            }
            foreach (var filter in instance.GetComponentsInChildren<MeshFilter>())
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null || filter.sharedMesh == null) continue;
                var b = renderer.bounds;
                Grow(ref total, b.min);
                Grow(ref total, b.max);
            }
            return total;
        }

        static void Grow(ref Bounds? total, Vector3 point)
        {
            if (total == null)
            {
                total = new Bounds(point, Vector3.zero);
                return;
            }
            var b = total.Value;
            b.Encapsulate(point);
            total = b;
        }
    }
}
