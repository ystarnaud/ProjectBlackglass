using System;
using UnityEngine;
using UnityEngine.UI;

namespace Blackglass
{
    /// <summary>
    /// One squad member's card (460 x 72): portrait badge with initials, name, "Role - Rank n" line, health bar and number,
    /// and the state column. Every state has a word as well as a colour: controlled = a filled square at the left edge and
    /// CONTROL, selected = an outline frame and SELECTED, down = a dimmed card and DOWN with an empty bar, and a companion
    /// tag in its own line. The card root is the pointer target (a Button), wired to requests in a later task; this class
    /// only keeps the last applied data and writes what changed.
    /// </summary>
    internal sealed class SquadCardView
    {
        public const float Width = 460f;
        public const float Height = 72f;
        const float DownAlpha = 0.45f;
        const float LowHealth = 0.25f;
        const string ControlWord = "CONTROL";
        const string SelectedWord = "SELECTED";
        const string DownWord = "DOWN";
        const string RankSeparator = " \u00B7 ";   // middle dot, in the built-in font

        readonly Image background;
        readonly CanvasGroup content;
        readonly RectTransform controlMark;
        readonly RectTransform portrait;
        readonly Image portraitImage;
        readonly Text initials;
        readonly Text nameLabel;
        readonly Text roleLabel;
        readonly RectTransform healthBar;
        readonly Image healthFill;
        readonly Text healthLabel;
        readonly Text controlLabel;
        readonly Text selectedLabel;
        readonly Text tagLabel;
        readonly Text downLabel;
        readonly RectTransform selectedFrame;
        bool applied;

        public SquadCardView(Transform parent, string name)
        {
            var root = HudFactory.Box(name, parent, HudTheme.Panel, true);
            Root = root;
            background = root.GetComponent<Image>();
            var button = root.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };

            var body = HudFactory.Rect("Content", root);
            content = body.gameObject.AddComponent<CanvasGroup>();

            controlMark = HudFactory.Box("ControlMark", body, HudTheme.Accent, false);
            HudFactory.Place(controlMark, new Vector2(0f, 0.5f), new Vector2(4f, 0f), new Vector2(8f, 8f));

            portrait = HudFactory.Box("Portrait", body, HudTheme.PanelEdge, false);
            HudFactory.Place(portrait, new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(48f, 48f));
            initials = HudFactory.Label("Initials", portrait, HudTheme.FontLarge, HudTheme.Text, TextAnchor.MiddleCenter);
            var art = HudFactory.Box("Art", portrait, Color.white, false);
            portraitImage = art.GetComponent<Image>();
            portraitImage.sprite = null;
            art.gameObject.SetActive(false);

            nameLabel = Line("Name", body, HudTheme.FontBody, HudTheme.Text, TextAnchor.MiddleLeft, 72f, 6f, 220f, 22f);
            roleLabel = Line("Role", body, HudTheme.FontSmall, HudTheme.TextDim, TextAnchor.MiddleLeft, 72f, 28f, 220f, 18f);

            var bar = HudFactory.Bar("HealthBar", body, HudTheme.PanelEdge, HudTheme.Good, out healthFill);
            healthBar = bar.rectTransform;
            HudFactory.Place(healthBar, new Vector2(0f, 1f), new Vector2(72f, -52f), new Vector2(140f, 10f));
            healthLabel = Line("Health", body, HudTheme.FontSmall, HudTheme.Text, TextAnchor.MiddleLeft, 220f, 47f, 72f, 18f);

            var states = HudFactory.Rect("States", body);
            HudFactory.Place(states, new Vector2(1f, 1f), new Vector2(-8f, -2f), new Vector2(148f, 68f));
            var stack = HudFactory.Stack(states, true, new RectOffset(), 0f, TextAnchor.UpperRight);
            stack.childForceExpandWidth = true;
            controlLabel = State("Control", states, ControlWord, HudTheme.Accent);
            selectedLabel = State("Selected", states, SelectedWord, HudTheme.Accent);
            tagLabel = State("Tag", states, string.Empty, HudTheme.Text);
            downLabel = State("Down", states, DownWord, HudTheme.Bad);

            // The selection frame is outside the dimmed content, so it stays crisp.
            selectedFrame = HudFactory.Rect("SelectedFrame", root);
            Edge(selectedFrame, "Top", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0f, 2f));
            Edge(selectedFrame, "Bottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(0f, 2f));
            Edge(selectedFrame, "Left", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(2f, 0f));
            Edge(selectedFrame, "Right", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(2f, 0f));

            controlMark.gameObject.SetActive(false);
            selectedFrame.gameObject.SetActive(false);
            controlLabel.gameObject.SetActive(false);
            selectedLabel.gameObject.SetActive(false);
            tagLabel.gameObject.SetActive(false);
            downLabel.gameObject.SetActive(false);
        }

        /// <summary>Raised with the card's unit and whether it was a double activation. Nothing raises it yet.</summary>
#pragma warning disable CS0067
        internal event Action<CommandableUnit, bool> Clicked;
#pragma warning restore CS0067

        internal RectTransform Root { get; }
        /// <summary>The data applied last (the unit for requests); empty before the first Apply.</summary>
        internal HudSquadCard Card { get; private set; }

        internal Image Background => background;
        internal RectTransform Portrait => portrait;
        internal Text InitialsLabel => initials;
        internal Text NameLabel => nameLabel;
        internal Text RoleLabel => roleLabel;
        internal RectTransform HealthBar => healthBar;
        internal Image HealthFill => healthFill;
        internal Text HealthLabel => healthLabel;
        internal Text ControlLabel => controlLabel;
        internal Text SelectedLabel => selectedLabel;
        internal Text TagLabel => tagLabel;
        internal Text DownLabel => downLabel;
        internal RectTransform ControlMark => controlMark;
        internal RectTransform SelectedFrame => selectedFrame;
        internal float ContentAlpha => content.alpha;

        public void Apply(HudSquadCard c)
        {
            var first = !applied;
            var previous = Card;
            if (first || !ReferenceEquals(previous.Unit, c.Unit))
                ShowPortrait(HudPortraits.Resolve(c.Unit));
            if (first || !SameFields(previous, c))
            {
                HudFactory.SetText(initials, c.Initials);
                HudFactory.SetText(nameLabel, c.Name);
                if (first || !string.Equals(previous.Role, c.Role, StringComparison.Ordinal) || previous.Rank != c.Rank)
                    HudFactory.SetText(roleLabel, RoleLine(c.Role, c.Rank));
                if (first || previous.Health != c.Health || previous.MaxHealth != c.MaxHealth)
                    HudFactory.SetText(healthLabel, HudText.Health(c.Health, c.MaxHealth));

                var fraction = c.IsDown || c.MaxHealth <= 0 ? 0f : (float)c.Health / c.MaxHealth;
                HudFactory.SetFill(healthFill, fraction);
                HudFactory.SetColor(healthFill, c.IsDown || fraction <= LowHealth ? HudTheme.Bad : HudTheme.Good);

                HudFactory.SetActive(controlMark.gameObject, c.IsControlled);
                HudFactory.SetActive(controlLabel.gameObject, c.IsControlled);
                HudFactory.SetActive(selectedFrame.gameObject, c.IsSelected);
                HudFactory.SetActive(selectedLabel.gameObject, c.IsSelected);
                var hasTag = !string.IsNullOrEmpty(c.Tag);
                HudFactory.SetActive(tagLabel.gameObject, hasTag);
                if (hasTag)
                    HudFactory.SetText(tagLabel, c.Tag);
                HudFactory.SetActive(downLabel.gameObject, c.IsDown);

                var alpha = c.IsDown ? DownAlpha : 1f;
                if (!Mathf.Approximately(content.alpha, alpha))
                    content.alpha = alpha;
            }
            Card = c;
            applied = true;
        }

        void ShowPortrait(Sprite sprite)
        {
            if (portraitImage.sprite != sprite)
                portraitImage.sprite = sprite;
            HudFactory.SetActive(portraitImage.gameObject, sprite != null);
            HudFactory.SetActive(initials.gameObject, sprite == null);
        }

        static string RoleLine(string role, int rank) =>
            rank > 0 ? role + RankSeparator + "Rank " + rank.ToString(System.Globalization.CultureInfo.InvariantCulture) : role ?? string.Empty;

        static bool SameFields(HudSquadCard a, HudSquadCard b) =>
            a.Rank == b.Rank && a.Health == b.Health && a.MaxHealth == b.MaxHealth
            && a.IsDown == b.IsDown && a.IsControlled == b.IsControlled && a.IsSelected == b.IsSelected
            && string.Equals(a.Name, b.Name, StringComparison.Ordinal) && string.Equals(a.Role, b.Role, StringComparison.Ordinal)
            && string.Equals(a.Initials, b.Initials, StringComparison.Ordinal) && string.Equals(a.Tag, b.Tag, StringComparison.Ordinal);

        static Text Line(string name, Transform parent, int size, Color color, TextAnchor anchor, float x, float y, float width, float height)
        {
            var label = HudFactory.Label(name, parent, size, color, anchor);
            label.verticalOverflow = VerticalWrapMode.Truncate;   // a long name never spills into the lines below
            HudFactory.Place(label.rectTransform, new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(width, height));
            return label;
        }

        static Text State(string name, Transform parent, string word, Color color)
        {
            var label = HudFactory.Label(name, parent, HudTheme.FontSmall, color, TextAnchor.MiddleRight);
            label.text = word;
            return label;
        }

        static void Edge(RectTransform frame, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 size)
        {
            var edge = HudFactory.Box(name, frame, HudTheme.Accent, false);
            HudFactory.Anchor(edge, anchorMin, anchorMax, pivot, Vector2.zero, Vector2.zero);
            edge.sizeDelta = size;
        }
    }
}
