using UnityEngine;

namespace Blackglass
{
    /// <summary>Debug feedback: shows a ring object while the unit is selected.</summary>
    [RequireComponent(typeof(SelectableUnit))]
    public sealed class SelectionIndicator : MonoBehaviour
    {
        [SerializeField] GameObject ring;

        SelectableUnit selectable;

        internal void Initialize(GameObject ringObject)
        {
            ring = ringObject;
            Show(Selectable.IsSelected);
        }

        SelectableUnit Selectable => selectable != null ? selectable : selectable = GetComponent<SelectableUnit>();

        void OnEnable()
        {
            Selectable.SelectionChanged += Show;
            Show(Selectable.IsSelected);
        }

        void OnDisable() => Selectable.SelectionChanged -= Show;

        void Show(bool selected)
        {
            if (ring != null)
                ring.SetActive(selected);
        }
    }
}
