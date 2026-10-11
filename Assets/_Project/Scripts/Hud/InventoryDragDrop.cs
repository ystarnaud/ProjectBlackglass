using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Blackglass
{
    /// <summary>
    /// Which command a drag from one list to another means. Dragging only picks the same command the buttons send, so every
    /// rule and refusal message stays in one place: a drop selects the dragged entry and invokes that command.
    /// </summary>
    public static class InventoryDragRules
    {
        public static bool TryGetCommand(InventoryListKind from, InventoryListKind to, out InventoryCommand command)
        {
            command = default;
            switch (from)
            {
                case InventoryListKind.Stash when to == InventoryListKind.Bag:
                    command = InventoryCommand.ToBag;
                    return true;
                case InventoryListKind.Stash when to == InventoryListKind.Slot:
                case InventoryListKind.Bag when to == InventoryListKind.Slot:
                    command = InventoryCommand.Equip;   // from the stash the same command moves the item first
                    return true;
                case InventoryListKind.Bag when to == InventoryListKind.Stash:
                    command = InventoryCommand.ToStash;
                    return true;
                case InventoryListKind.Slot when to == InventoryListKind.Bag:
                    command = InventoryCommand.Unequip;
                    return true;
                case InventoryListKind.Container when to == InventoryListKind.Bag:
                    command = InventoryCommand.Take;
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Mouse drag and drop for the inventory panel: rows and equipment slots can be dragged, and the lists, the equipment block
    /// and the empty space around them take drops. A small label follows the pointer while dragging. Buttons and controller
    /// navigation work exactly as before; this layer only adds a pointer path to the same commands.
    /// </summary>
    internal sealed class InventoryDragLayer
    {
        readonly Func<InventoryListKind, int, string> idAt;
        readonly Action<InventoryListKind, string, InventoryCommand> perform;
        readonly Action<string> say;
        readonly Action<InventoryListKind, string> select;
        readonly RectTransform root;
        readonly RectTransform proxy;
        readonly Text proxyLabel;
        bool active;
        InventoryListKind fromKind;
        string fromId;

        public InventoryDragLayer(RectTransform root, Func<InventoryListKind, int, string> idAt,
            Action<InventoryListKind, string, InventoryCommand> perform, Action<InventoryListKind, string> select, Action<string> say)
        {
            this.root = root;
            this.idAt = idAt;
            this.perform = perform;
            this.select = select;
            this.say = say;
            proxy = HudFactory.Box("DragProxy", root, new Color(HudTheme.Accent.r, HudTheme.Accent.g, HudTheme.Accent.b, 0.85f), false);
            HudFactory.Place(proxy, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(320f, 34f));
            proxyLabel = HudFactory.Label("Label", proxy, HudTheme.FontBody, HudTheme.Text, TextAnchor.MiddleLeft);
            HudFactory.Place(proxyLabel.rectTransform, new Vector2(0f, 0.5f), new Vector2(10f, 0f), new Vector2(300f, 34f));
            proxyLabel.raycastTarget = false;
            proxy.gameObject.SetActive(false);
        }

        public bool IsDragging => active;

        /// <summary>Makes a row or slot button draggable.</summary>
        public void AttachSource(GameObject target, InventoryListKind kind, int index)
        {
            var source = target.AddComponent<InventoryDragSource>();
            source.layer = this;
            source.kind = kind;
            source.index = index;
        }

        /// <summary>Makes an object take drops that mean "into this list".</summary>
        public void AttachZone(GameObject target, InventoryListKind kind)
        {
            var zone = target.AddComponent<InventoryDropZone>();
            zone.layer = this;
            zone.kind = kind;
        }

        internal void Begin(InventoryListKind kind, int index, string text, PointerEventData eventData)
        {
            Cancel();
            if (eventData.button != PointerEventData.InputButton.Left)
                return;
            var id = idAt(kind, index);
            if (string.IsNullOrEmpty(id))
                return;   // an empty slot or a row that has gone: nothing to carry
            active = true;
            fromKind = kind;
            fromId = id;
            HudFactory.SetText(proxyLabel, text);
            proxy.gameObject.SetActive(true);
            proxy.SetAsLastSibling();
            Move(eventData);
        }

        internal void Move(PointerEventData eventData)
        {
            if (!active)
                return;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(root, eventData.position, null, out var local))
                proxy.anchoredPosition = local + new Vector2(160f, -22f);
        }

        internal void Drop(InventoryListKind target)
        {
            if (!active)
                return;
            var from = fromKind;
            var id = fromId;
            Cancel();
            if (from == target)
            {
                select(from, id);   // a click that wandered past the drag threshold still selects the row
                return;
            }
            if (InventoryDragRules.TryGetCommand(from, target, out var command))
                perform(from, id, command);
            else
                say(from == InventoryListKind.Slot ? "Drop equipment on the bag to unequip it." : "That can't go there.");
        }

        /// <summary>Ends a drag without doing anything (a drop outside every zone, or the panel closing).</summary>
        public void Cancel()
        {
            active = false;
            fromId = null;
            proxy.gameObject.SetActive(false);
        }
    }

    internal sealed class InventoryDragSource : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        internal InventoryDragLayer layer;
        internal InventoryListKind kind;
        internal int index;

        public void OnBeginDrag(PointerEventData eventData)
        {
            var label = GetComponentInChildren<Text>();
            layer.Begin(kind, index, label != null ? label.text : string.Empty, eventData);
        }

        public void OnDrag(PointerEventData eventData) => layer.Move(eventData);

        // A drop on a zone has already been handled (OnDrop runs before OnEndDrag); anything still active ended off every zone.
        public void OnEndDrag(PointerEventData eventData) => layer.Cancel();
    }

    internal sealed class InventoryDropZone : MonoBehaviour, IDropHandler
    {
        internal InventoryDragLayer layer;
        internal InventoryListKind kind;

        public void OnDrop(PointerEventData eventData) => layer.Drop(kind);
    }
}
