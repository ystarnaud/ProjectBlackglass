using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Marks a box obstacle as cover-generating geometry. Carries no data: the generator reads the box. A mission
    /// generator tags the geometry it spawns the same way. Upright boxes only; only the yaw of the rotation is used.
    /// Low boxes (top at or below 1.2 m) get cover along every face; tall boxes only at the outward-opening ends of long
    /// walls; pillars and stubs none (see <see cref="CoverGenerator"/>).
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public sealed class CoverSurface : MonoBehaviour
    {
        public CoverBox ToBox()
        {
            var box = GetComponent<BoxCollider>();
            var lossy = transform.lossyScale;
            var size = Vector3.Scale(box.size, new Vector3(Mathf.Abs(lossy.x), Mathf.Abs(lossy.y), Mathf.Abs(lossy.z)));
            return new CoverBox(name, transform.TransformPoint(box.center), transform.eulerAngles.y, size * 0.5f, box);
        }
    }
}
