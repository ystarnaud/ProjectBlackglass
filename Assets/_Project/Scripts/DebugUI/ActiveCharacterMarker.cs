using UnityEngine;

namespace Blackglass
{
    /// <summary>
    /// Debug feedback: keeps a visual floating above the active character's head, and hides it when there is none.
    /// Lives on its own object (not on a unit), so it follows whoever is active. Works while paused.
    /// </summary>
    public sealed class ActiveCharacterMarker : MonoBehaviour
    {
        [SerializeField] ActiveCharacter activeCharacter;
        [SerializeField] GameObject visual;
        // Height above the unit's pivot (the capsule's centre; it is 2 m tall).
        [SerializeField] float height = 1.6f;

        internal void Initialize(ActiveCharacter active, GameObject visualObject)
        {
            activeCharacter = active;
            visual = visualObject;
        }

        void LateUpdate()
        {
            var show = activeCharacter != null && activeCharacter.HasUnit;
            if (show)
                transform.position = activeCharacter.Unit.transform.position + Vector3.up * height;
            if (visual != null && visual.activeSelf != show)
                visual.SetActive(show);
        }
    }
}
