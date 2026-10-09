using UnityEngine;
using UnityEngine.UI;

namespace Blackglass
{
    /// <summary>
    /// Bottom right, above the prompts (340 x 140): the hostile the player is aiming at, hovering, has targeted or is
    /// attacking. Hostility reads as the word HOSTILE and a filled diamond, not as a colour alone. Name, detail, health bar
    /// with numbers and the kind of attention (AIMING, HOVERED, TARGETED, ATTACKING) are the snapshot's; the cover line is
    /// drawn only while the game is paused or the target is being attacked, so a hovering pointer does not flicker with
    /// hit-chance changes. Hidden without a target; nothing in it is a pointer target.
    /// </summary>
    internal sealed class TargetPanel : HudPanel
    {
        public const float Width = 340f;
        public const float Height = 140f;
        const float Pad = 12f;
        const float ZoneGap = 16f;
        const float DiamondSide = 10f;
        const string HostileWord = "HOSTILE";
        const string AttackingTag = "ATTACKING";
        public static readonly Vector2 Size = new Vector2(Width, Height);

        readonly Image diamond;
        readonly Text hostileTag;
        readonly Text tagLabel;
        readonly Text nameLabel;
        readonly Text detailLabel;
        readonly Image healthFill;
        readonly Text healthLabel;
        readonly Text coverLabel;
        int shownHealth = -1, shownMax = -1;

        public TargetPanel(Transform parent) : base(HudFactory.Box("Target", parent, HudTheme.Panel, false))
        {
            HudFactory.Place(Root, new Vector2(1f, 0f), new Vector2(-HudTheme.Margin, HudTheme.Margin + PromptPanel.Height + ZoneGap), Size);

            var diamondBox = HudFactory.Box("HostileDiamond", Root, HudTheme.Bad, false);
            diamond = diamondBox.GetComponent<Image>();
            HudFactory.Anchor(diamondBox, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            diamondBox.sizeDelta = new Vector2(DiamondSide, DiamondSide);
            diamondBox.anchoredPosition = new Vector2(Pad + 8f, -20f);
            diamondBox.localRotation = Quaternion.Euler(0f, 0f, 45f);

            hostileTag = Line("HostileTag", HudTheme.FontBody, HudTheme.Bad, TextAnchor.MiddleLeft, 34f, 9f, 110f, 22f);
            hostileTag.text = HostileWord;
            tagLabel = Line("Tag", HudTheme.FontBody, HudTheme.Text, TextAnchor.MiddleRight, Width - Pad - 160f, 9f, 160f, 22f);
            nameLabel = Line("Name", HudTheme.FontLarge, HudTheme.Text, TextAnchor.MiddleLeft, Pad, 36f, Width - 2f * Pad, 28f);
            detailLabel = Line("Detail", HudTheme.FontBody, HudTheme.TextDim, TextAnchor.MiddleLeft, Pad, 66f, Width - 2f * Pad, 22f);
            var bar = HudFactory.Bar("HealthBar", Root, HudTheme.PanelEdge, HudTheme.Bad, out healthFill);
            Top(bar.rectTransform, Pad, 96f, 200f, 10f);
            healthLabel = Line("Health", HudTheme.FontBody, HudTheme.Text, TextAnchor.MiddleLeft, 220f, 91f, 108f, 20f);
            coverLabel = Line("Cover", HudTheme.FontBody, HudTheme.Text, TextAnchor.MiddleLeft, Pad, 112f, Width - 2f * Pad, 22f);
            coverLabel.gameObject.SetActive(false);

            Root.gameObject.SetActive(false);
        }

        internal Image Diamond => diamond;
        internal Text HostileTag => hostileTag;
        internal Text TagLabel => tagLabel;
        internal Text NameLabel => nameLabel;
        internal Text DetailLabel => detailLabel;
        internal Image HealthFill => healthFill;
        internal RectTransform HealthBar => healthFill.rectTransform.parent as RectTransform;
        internal Text HealthLabel => healthLabel;
        internal Text CoverLabel => coverLabel;

        public override void Apply(HudSnapshot s)
        {
            var target = s.Target;
            HudFactory.SetActive(Root.gameObject, target.Visible);
            if (!target.Visible)
                return;

            HudFactory.SetText(nameLabel, target.Name);
            HudFactory.SetText(detailLabel, target.Detail);
            HudFactory.SetText(tagLabel, target.Tag);

            if (shownHealth != target.Health || shownMax != target.MaxHealth)
            {
                shownHealth = target.Health;
                shownMax = target.MaxHealth;
                HudFactory.SetText(healthLabel, HudText.Health(shownHealth, shownMax));
                HudFactory.SetFill(healthFill, shownMax <= 0 ? 0f : (float)shownHealth / shownMax);
            }

            var cover = target.CoverText;
            var showCover = !string.IsNullOrEmpty(cover)
                && (s.IsPaused || string.Equals(target.Tag, AttackingTag, System.StringComparison.Ordinal));
            HudFactory.SetActive(coverLabel.gameObject, showCover);
            if (showCover)
                HudFactory.SetText(coverLabel, cover);
        }

        static void Top(RectTransform r, float x, float y, float width, float height) =>
            HudFactory.Place(r, new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(width, height));

        Text Line(string name, int size, Color color, TextAnchor anchor, float x, float y, float width, float height)
        {
            var label = HudFactory.Label(name, Root, size, color, anchor);
            label.verticalOverflow = VerticalWrapMode.Truncate;   // a long name never spills into the lines below
            Top(label.rectTransform, x, y, width, height);
            return label;
        }
    }
}
