using System;
using UnityEngine;
using UnityEngine.UI;

namespace Blackglass
{
    /// <summary>
    /// One ability slot (170 x 64): prompt chip, name and a state word. The state is always a word as well as a colour:
    /// READY, the cooldown seconds (over a vertical fill that darkens by the cooldown fraction), ARMED (plus a bright frame
    /// and a "&gt; " name prefix) or an em dash (plus a dimmed slot). The slot root is the pointer target (a Button), wired
    /// to a request in a later task; this class only keeps the last applied data and writes what changed.
    /// </summary>
    internal sealed class AbilitySlotView
    {
        public const float Width = 170f;
        public const float Height = 64f;
        const float UnavailableAlpha = 0.45f;
        const string ReadyWord = "READY";
        const string ArmedWord = "ARMED";
        const string UnavailableWord = "—";   // em dash; the HUD tests check the built-in font has it
        const string ArmedPrefix = "> ";
        static readonly Color SlotBack = new Color(0.10f, 0.12f, 0.16f, 0.92f);
        static readonly Color ArmedBack = new Color(0.12f, 0.28f, 0.36f, 0.96f);
        static readonly Color CooldownShade = new Color(0f, 0f, 0f, 0.62f);

        readonly Image background;
        readonly CanvasGroup content;
        readonly Image cooldownOverlay;
        readonly RectTransform chip;
        readonly Text promptLabel;
        readonly Text nameLabel;
        readonly Text stateLabel;
        readonly RectTransform armedFrame;
        bool applied;
        string shownName;
        bool shownArmed;
        int shownSecondsKey = int.MinValue;

        public AbilitySlotView(Transform parent, string name)
        {
            var root = HudFactory.Box(name, parent, SlotBack, true);
            Root = root;
            background = root.GetComponent<Image>();
            var button = root.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };

            var body = HudFactory.Rect("Content", root);
            content = body.gameObject.AddComponent<CanvasGroup>();

            var overlay = HudFactory.Box("Cooldown", body, CooldownShade, false);
            cooldownOverlay = overlay.GetComponent<Image>();
            cooldownOverlay.type = Image.Type.Filled;
            cooldownOverlay.fillMethod = Image.FillMethod.Vertical;
            cooldownOverlay.fillOrigin = (int)Image.OriginVertical.Top;
            cooldownOverlay.fillAmount = 0f;
            overlay.gameObject.SetActive(false);

            chip = HudFactory.Box("PromptChip", body, HudTheme.PanelEdge, false);
            HudFactory.Place(chip, new Vector2(0f, 1f), new Vector2(6f, -5f), new Vector2(78f, 18f));
            promptLabel = Line("Prompt", chip, HudTheme.FontSmall, HudTheme.Text, TextAnchor.MiddleCenter);
            HudFactory.Anchor(promptLabel.rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

            stateLabel = Line("State", body, HudTheme.FontBody, HudTheme.Accent, TextAnchor.MiddleRight);
            HudFactory.Place(stateLabel.rectTransform, new Vector2(0f, 1f), new Vector2(88f, -4f), new Vector2(76f, 20f));

            nameLabel = Line("Name", body, HudTheme.FontBody, HudTheme.Text, TextAnchor.UpperLeft);
            HudFactory.Place(nameLabel.rectTransform, new Vector2(0f, 1f), new Vector2(6f, -26f), new Vector2(158f, 36f));

            // The frame is outside the dimmed content, so an armed slot's frame is always crisp.
            armedFrame = HudFactory.Rect("ArmedFrame", root);
            SquadCardView.Edge(armedFrame, "Top", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0f, 2f));
            SquadCardView.Edge(armedFrame, "Bottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(0f, 2f));
            SquadCardView.Edge(armedFrame, "Left", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(2f, 0f));
            SquadCardView.Edge(armedFrame, "Right", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(2f, 0f));
            armedFrame.gameObject.SetActive(false);
        }

        /// <summary>Raised with the slot index when the slot is clicked. Nothing raises it yet.</summary>
#pragma warning disable CS0067
        internal event Action<int> Clicked;
#pragma warning restore CS0067

        internal RectTransform Root { get; }
        /// <summary>The data applied last (its Slot is what a click requests); empty before the first Apply.</summary>
        internal HudAbilitySlot Slot { get; private set; }

        internal Image Background => background;
        internal RectTransform PromptChip => chip;
        internal Text PromptLabel => promptLabel;
        internal Text NameLabel => nameLabel;
        internal Text StateLabel => stateLabel;
        internal Image CooldownOverlay => cooldownOverlay;
        internal RectTransform ArmedFrame => armedFrame;
        internal float ContentAlpha => content.alpha;

        public void Apply(HudAbilitySlot a)
        {
            var first = !applied;
            HudFactory.SetText(promptLabel, a.Prompt);

            var armed = a.State == HudAbilityState.Armed;
            if (first || armed != shownArmed || !string.Equals(shownName, a.Name, StringComparison.Ordinal))
            {
                shownName = a.Name;
                shownArmed = armed;
                HudFactory.SetText(nameLabel, armed ? ArmedPrefix + a.Name : a.Name);
            }

            switch (a.State)
            {
                case HudAbilityState.Cooldown:
                    var key = SecondsKey(a.Remaining);
                    if (key != shownSecondsKey)
                    {
                        shownSecondsKey = key;
                        HudFactory.SetText(stateLabel, HudText.Seconds(a.Remaining));
                    }
                    HudFactory.SetColor(stateLabel, HudTheme.Warn);
                    break;
                case HudAbilityState.Armed:
                    shownSecondsKey = int.MinValue;
                    HudFactory.SetText(stateLabel, ArmedWord);
                    HudFactory.SetColor(stateLabel, HudTheme.Accent);
                    break;
                case HudAbilityState.Unavailable:
                    shownSecondsKey = int.MinValue;
                    HudFactory.SetText(stateLabel, UnavailableWord);
                    HudFactory.SetColor(stateLabel, HudTheme.TextDim);
                    break;
                default:
                    shownSecondsKey = int.MinValue;
                    HudFactory.SetText(stateLabel, ReadyWord);
                    HudFactory.SetColor(stateLabel, HudTheme.Accent);
                    break;
            }

            var cooling = a.State == HudAbilityState.Cooldown;
            HudFactory.SetActive(cooldownOverlay.gameObject, cooling);
            if (cooling)
                HudFactory.SetFill(cooldownOverlay, a.Fraction);
            HudFactory.SetActive(armedFrame.gameObject, armed);
            HudFactory.SetColor(background, armed ? ArmedBack : SlotBack);
            var alpha = a.State == HudAbilityState.Unavailable ? UnavailableAlpha : 1f;
            if (!Mathf.Approximately(content.alpha, alpha))
                content.alpha = alpha;

            Slot = a;
            applied = true;
        }

        /// <summary>
        /// A key that changes exactly when HudText.Seconds' output does (tenths below ten seconds, whole seconds above), so the
        /// number is formatted and written only then, never once per frame.
        /// </summary>
        static int SecondsKey(float seconds)
        {
            var tenths = Mathf.Ceil(Mathf.Max(0f, seconds) * 10f);
            return tenths >= 100f ? -Mathf.CeilToInt(seconds) : (int)tenths;
        }

        static Text Line(string name, Transform parent, int size, Color color, TextAnchor anchor)
        {
            var label = HudFactory.Label(name, parent, size, color, anchor);
            label.verticalOverflow = VerticalWrapMode.Truncate;   // a long name never spills out of the slot
            return label;
        }
    }
}
