using System;
using UnityEngine;
using UnityEngine.UI;

namespace Blackglass
{
    /// <summary>
    /// Bottom centre (760 x 280): the controlled operative (name, role and rank, health, cover), the ability slots with the
    /// aiming strip, and the command queue with its CLEAR button. Without a controlled unit it says so and shows nothing
    /// else. The queue lists up to eight steps in two columns of four, so the zone stays low; the step marked current
    /// starts with "&gt; " and the others with their number. Slots and CLEAR are pointer targets that raise their Clicked
    /// events; otherwise this class only draws the snapshot and writes what changed.
    /// </summary>
    internal sealed class OperativePanel : HudPanel
    {
        public const int MaxSlots = 4;
        public const int MaxSteps = 8;
        public const float Width = 760f;
        public const float Height = 280f;
        const float Pad = 12f;
        const float SlotGap = 18f;
        const float StepHeight = 20f;
        const int StepRows = 4;
        const float StepColumnWidth = 360f;
        const float LowHealth = 0.25f;
        const string NoUnitWord = "No unit in control";
        const string OrdersPrefix = "ORDERS: ";
        const string CurrentPrefix = "> ";
        const string ClearWord = "CLEAR";
        public static readonly Vector2 Size = new Vector2(Width, Height);

        readonly Text noUnit;
        readonly RectTransform info;
        readonly Text nameLabel;
        readonly Text roleLabel;
        readonly Image healthFill;
        readonly Text healthLabel;
        readonly Text coverLabel;
        readonly RectTransform abilities;
        readonly AbilitySlotView[] slots = new AbilitySlotView[MaxSlots];
        readonly RectTransform strip;
        readonly Text stripLabel;
        readonly RectTransform queue;
        readonly Text ordersLabel;
        readonly Text moreLabel;
        readonly RectTransform clearButton;
        readonly Image clearBackground;
        readonly Text[] steps = new Text[MaxSteps];
        readonly int[] shownNumbers = new int[MaxSteps];
        readonly bool[] shownCurrent = new bool[MaxSteps];
        readonly string[] shownStepText = new string[MaxSteps];
        readonly string[] composed = new string[MaxSteps];

        string shownName, shownRole, shownOwner;
        int shownRank = -1, shownHealth = -1, shownMax = -1, shownHidden = -1;

        public OperativePanel(Transform parent) : base(HudFactory.Box("Operative", parent, HudTheme.Panel, false))
        {
            HudFactory.Place(Root, new Vector2(0.5f, 0f), new Vector2(0f, HudTheme.Margin), Size);

            noUnit = HudFactory.Label("NoUnit", Root, HudTheme.FontLarge, HudTheme.TextDim, TextAnchor.MiddleLeft);
            noUnit.text = NoUnitWord;
            Top(noUnit.rectTransform, Pad, 8f, 400f, 28f);

            info = HudFactory.Rect("Info", Root);
            nameLabel = Line("Name", info, HudTheme.FontLarge, HudTheme.Text, TextAnchor.MiddleLeft, Pad, 8f, 330f, 28f);
            roleLabel = Line("Role", info, HudTheme.FontBody, HudTheme.TextDim, TextAnchor.MiddleLeft, 350f, 8f, 260f, 28f);
            var bar = HudFactory.Bar("HealthBar", info, HudTheme.PanelEdge, HudTheme.Good, out healthFill);
            Top(bar.rectTransform, Pad, 46f, 200f, 10f);
            healthLabel = Line("Health", info, HudTheme.FontBody, HudTheme.Text, TextAnchor.MiddleLeft, 220f, 41f, 90f, 20f);
            coverLabel = Line("Cover", info, HudTheme.FontBody, HudTheme.Text, TextAnchor.MiddleLeft, 320f, 41f, 300f, 20f);

            abilities = HudFactory.Rect("Abilities", Root);
            for (var i = 0; i < MaxSlots; i++)
            {
                slots[i] = new AbilitySlotView(abilities, "Slot" + i);
                Top(slots[i].Root, Pad + i * (AbilitySlotView.Width + SlotGap), 66f, AbilitySlotView.Width, AbilitySlotView.Height);
                slots[i].Root.gameObject.SetActive(false);
            }
            var stripBox = HudFactory.Box("AimingStrip", abilities, new Color(HudTheme.Accent.r, HudTheme.Accent.g, HudTheme.Accent.b, 0.16f), false);
            strip = stripBox;
            Top(strip, Pad, 136f, Width - 2f * Pad, 22f);
            stripLabel = Line("Text", strip, HudTheme.FontBody, HudTheme.Accent, TextAnchor.MiddleLeft, 8f, 0f, Width - 2f * Pad - 16f, 22f);
            strip.gameObject.SetActive(false);

            queue = HudFactory.Rect("Queue", Root);
            ordersLabel = Line("Orders", queue, HudTheme.FontBody, HudTheme.Text, TextAnchor.MiddleLeft, Pad, 164f, 330f, 22f);
            moreLabel = Line("More", queue, HudTheme.FontBody, HudTheme.TextDim, TextAnchor.MiddleRight, 440f, 164f, 200f, 22f);
            clearButton = HudFactory.Box("Clear", queue, HudTheme.PanelEdge, true);
            clearBackground = clearButton.GetComponent<Image>();
            Top(clearButton, Width - Pad - 96f, 164f, 96f, 22f);
            var button = clearButton.gameObject.AddComponent<Button>();
            button.targetGraphic = clearBackground;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(() => ClearClicked?.Invoke(QueueUnit));
            var clearLabel = HudFactory.Label("Label", clearButton, HudTheme.FontSmall, HudTheme.Warn, TextAnchor.MiddleCenter);
            clearLabel.text = ClearWord;
            clearButton.gameObject.SetActive(false);
            moreLabel.gameObject.SetActive(false);
            for (var i = 0; i < MaxSteps; i++)
            {
                var column = i / StepRows;
                var row = i % StepRows;
                steps[i] = Line("Step" + i, queue, HudTheme.FontBody, HudTheme.Text, TextAnchor.MiddleLeft,
                    Pad + column * (StepColumnWidth + Pad), 188f + row * StepHeight, StepColumnWidth, StepHeight);
                steps[i].gameObject.SetActive(false);
                shownNumbers[i] = -1;
            }

            info.gameObject.SetActive(false);
            abilities.gameObject.SetActive(false);
            queue.gameObject.SetActive(false);
        }

        /// <summary>Raised with the queue's unit (as of the last Apply) when CLEAR is clicked.</summary>
        internal event Action<CommandableUnit> ClearClicked;

        /// <summary>The unit the queue shows, as of the last Apply (what CLEAR would stop); null without a controlled unit.</summary>
        internal CommandableUnit QueueUnit { get; private set; }

        internal int SlotCapacity => MaxSlots;
        internal int StepCapacity => MaxSteps;
        internal AbilitySlotView SlotAt(int i) => slots[i];
        internal Text StepAt(int i) => steps[i];
        internal Text NoUnitLabel => noUnit;
        internal RectTransform InfoGroup => info;
        internal RectTransform AbilityGroup => abilities;
        internal RectTransform QueueGroup => queue;
        internal Text NameLabel => nameLabel;
        internal Text RoleLabel => roleLabel;
        internal Image HealthFill => healthFill;
        internal RectTransform HealthBar => healthFill.rectTransform.parent as RectTransform;
        internal Text HealthLabel => healthLabel;
        internal Text CoverLabel => coverLabel;
        internal RectTransform AimingStrip => strip;
        internal Text AimingLabel => stripLabel;
        internal Text OrdersLabel => ordersLabel;
        internal Text MoreLabel => moreLabel;
        internal RectTransform ClearButton => clearButton;
        internal Image ClearBackground => clearBackground;

        public override void Apply(HudSnapshot s)
        {
            var has = s.HasControlled;
            QueueUnit = s.QueueUnit;
            HudFactory.SetActive(noUnit.gameObject, !has);
            HudFactory.SetActive(info.gameObject, has);
            HudFactory.SetActive(abilities.gameObject, has);
            HudFactory.SetActive(queue.gameObject, has);
            if (!has)
            {
                // Hidden groups keep no target names: the aiming line and the steps may name a hostile.
                HudFactory.SetText(stripLabel, string.Empty);
                for (var i = 0; i < MaxSteps; i++)
                    BlankStep(i);
                return;
            }

            ApplyInfo(s);
            ApplyAbilities(s);
            ApplyQueue(s);
        }

        void ApplyInfo(HudSnapshot s)
        {
            var name = s.ControlledName ?? string.Empty;
            if (shownName == null || !string.Equals(shownName, name, StringComparison.Ordinal))
            {
                shownName = name;
                HudFactory.SetText(nameLabel, name);
            }

            var role = s.ControlledRole ?? string.Empty;
            if (shownRole == null || shownRank != s.ControlledRank || !string.Equals(shownRole, role, StringComparison.Ordinal))
            {
                shownRole = role;
                shownRank = s.ControlledRank;
                HudFactory.SetText(roleLabel, SquadCardView.RoleLine(role, s.ControlledRank));
            }

            if (shownHealth != s.ControlledHealth || shownMax != s.ControlledMaxHealth)
            {
                shownHealth = s.ControlledHealth;
                shownMax = s.ControlledMaxHealth;
                HudFactory.SetText(healthLabel, HudText.Health(shownHealth, shownMax));
                var fraction = shownMax <= 0 ? 0f : (float)shownHealth / shownMax;
                HudFactory.SetFill(healthFill, fraction);
                HudFactory.SetColor(healthFill, fraction <= LowHealth ? HudTheme.Bad : HudTheme.Good);
            }

            HudFactory.SetText(coverLabel, s.ControlledCover);
        }

        void ApplyAbilities(HudSnapshot s)
        {
            var count = Mathf.Min(s.Abilities.Count, MaxSlots);
            for (var i = 0; i < MaxSlots; i++)
            {
                var visible = i < count;
                HudFactory.SetActive(slots[i].Root.gameObject, visible);
                if (visible)
                    slots[i].Apply(s.Abilities[i]);
            }

            HudFactory.SetActive(strip.gameObject, s.IsArmed);
            HudFactory.SetText(stripLabel, s.IsArmed ? s.ArmedLine : string.Empty);
        }

        void ApplyQueue(HudSnapshot s)
        {
            var owner = s.QueueOwner ?? string.Empty;
            var hasOwner = owner.Length > 0;
            HudFactory.SetActive(ordersLabel.gameObject, hasOwner);
            if (hasOwner && (shownOwner == null || !string.Equals(shownOwner, owner, StringComparison.Ordinal)))
            {
                shownOwner = owner;
                HudFactory.SetText(ordersLabel, OrdersPrefix + owner);
            }

            var count = hasOwner ? Mathf.Min(s.Queue.Count, MaxSteps) : 0;
            for (var i = 0; i < MaxSteps; i++)
            {
                var visible = i < count;
                HudFactory.SetActive(steps[i].gameObject, visible);
                if (visible)
                    ApplyStep(i, s.Queue[i]);
                else
                    BlankStep(i);
            }

            var hidden = hasOwner ? Mathf.Max(s.QueueHidden, 0) : 0;
            HudFactory.SetActive(moreLabel.gameObject, hidden > 0);
            if (hidden > 0 && hidden != shownHidden)
            {
                shownHidden = hidden;
                HudFactory.SetText(moreLabel, "+" + hidden.ToString(System.Globalization.CultureInfo.InvariantCulture) + " more");
            }

            HudFactory.SetActive(clearButton.gameObject, hasOwner && s.CanClearOrders);
        }

        void BlankStep(int i)
        {
            if (shownNumbers[i] == -1)
                return;
            shownNumbers[i] = -1;
            shownStepText[i] = null;
            HudFactory.SetText(steps[i], string.Empty);
        }

        void ApplyStep(int i, HudCommandStep step)
        {
            if (shownNumbers[i] != step.Number || shownCurrent[i] != step.IsCurrent
                || !string.Equals(shownStepText[i], step.Text, StringComparison.Ordinal))
            {
                shownNumbers[i] = step.Number;
                shownCurrent[i] = step.IsCurrent;
                shownStepText[i] = step.Text;
                composed[i] = (step.IsCurrent ? CurrentPrefix : step.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ")
                    + step.Text;
            }
            HudFactory.SetText(steps[i], composed[i]);
            HudFactory.SetColor(steps[i], step.IsCurrent ? HudTheme.Accent : HudTheme.Text);
        }

        /// <summary>Pins `r` by its top-left corner, `x` right and `y` down from the panel's top-left.</summary>
        static void Top(RectTransform r, float x, float y, float width, float height) =>
            HudFactory.Place(r, new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(width, height));

        static Text Line(string name, Transform parent, int size, Color color, TextAnchor anchor, float x, float y, float width, float height)
        {
            var label = HudFactory.Label(name, parent, size, color, anchor);
            label.verticalOverflow = VerticalWrapMode.Truncate;   // a long name never spills into the lines below
            Top(label.rectTransform, x, y, width, height);
            return label;
        }
    }
}
