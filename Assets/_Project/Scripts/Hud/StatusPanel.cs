using System;
using UnityEngine;
using UnityEngine.UI;

namespace Blackglass
{
    /// <summary>
    /// The mission-wide status: the tactical pause banner at top centre (framed, with a double-bar glyph so pause is not
    /// shown by colour alone), the mission result banner below it, and the top-right block with the extraction state, the
    /// follow chip and the pause chip. The two chips are pointer targets that raise FollowClicked / PauseClicked. The panel
    /// root is a non-drawing full-screen rect; its three blocks are the parts that take space.
    /// </summary>
    internal sealed class StatusPanel : HudPanel
    {
        public const float RightWidth = 320f;
        /// <summary>Height kept free for the pause banner, so the result banner below never moves.</summary>
        const float PauseReserve = 56f;
        const float BlockGap = 8f;
        static readonly string FailedBanner = MissionHudText.Banner(MissionPhase.Failure);

        readonly RectTransform pauseBanner;
        readonly Text pauseLabel;
        readonly Image[] pauseGlyph;
        readonly RectTransform resultBanner;
        readonly Text resultLabel;
        readonly RectTransform rightBlock;
        readonly RectTransform extractionChip;
        readonly Text extractionLabel;
        readonly RectTransform followChip;
        readonly Text followLabel;
        readonly RectTransform pauseChip;
        readonly Text pauseChipLabel;

        string shownResume;
        string shownBanner;
        bool hasExtraction;
        HudExtractionState shownExtraction;
        int shownInside;
        int shownRequired;

        public StatusPanel(Transform parent) : base(HudFactory.Rect("Status", parent))
        {
            // Pause banner: a Warn frame around a dark box holding the glyph and the text.
            pauseBanner = HudFactory.Box("PauseBanner", Root, HudTheme.Warn, false);
            HudFactory.Place(pauseBanner, new Vector2(0.5f, 1f), new Vector2(0f, -HudTheme.Margin), Vector2.zero);
            HudFactory.Stack(pauseBanner, false, new RectOffset(2, 2, 2, 2), 0f, TextAnchor.MiddleCenter);
            HudFactory.FitContent(pauseBanner, true, true);
            var pauseInner = HudFactory.Box("Inner", pauseBanner, HudTheme.Panel, false);
            HudFactory.Stack(pauseInner, false, new RectOffset(16, 16, 8, 8), 12f, TextAnchor.MiddleCenter);
            var glyph = HudFactory.Rect("Glyph", pauseInner);
            HudFactory.Size(glyph, 16f, 22f);
            pauseGlyph = new[] { GlyphBar("BarLeft", glyph, 0f), GlyphBar("BarRight", glyph, 1f) };
            pauseLabel = HudFactory.Label("Label", pauseInner, HudTheme.FontLarge, HudTheme.Warn, TextAnchor.MiddleLeft);
            pauseBanner.gameObject.SetActive(false);

            // Result banner: kept clear of the pause banner's reserved height.
            resultBanner = HudFactory.Box("ResultBanner", Root, HudTheme.PanelEdge, false);
            HudFactory.Place(resultBanner, new Vector2(0.5f, 1f), new Vector2(0f, -(HudTheme.Margin + PauseReserve + BlockGap)), Vector2.zero);
            HudFactory.Stack(resultBanner, false, new RectOffset(2, 2, 2, 2), 0f, TextAnchor.MiddleCenter);
            HudFactory.FitContent(resultBanner, true, true);
            var resultInner = HudFactory.Box("Inner", resultBanner, HudTheme.Panel, false);
            HudFactory.Stack(resultInner, false, new RectOffset(24, 24, 10, 10), 0f, TextAnchor.MiddleCenter);
            resultLabel = HudFactory.Label("Label", resultInner, HudTheme.FontBanner, HudTheme.Good, TextAnchor.MiddleCenter);
            resultBanner.gameObject.SetActive(false);

            // Top right: extraction state above the follow chip.
            rightBlock = HudFactory.Rect("StatusRight", Root);
            HudFactory.Place(rightBlock, new Vector2(1f, 1f), new Vector2(-HudTheme.Margin, -HudTheme.Margin), new Vector2(RightWidth, 0f));
            var stack = HudFactory.Stack(rightBlock, true, new RectOffset(), BlockGap, TextAnchor.UpperRight);
            stack.childForceExpandWidth = true;
            HudFactory.FitContent(rightBlock, false, true);
            extractionChip = Chip("Extraction", rightBlock, out extractionLabel);
            followChip = Chip("Follow", rightBlock, out followLabel);
            pauseChip = Chip("PauseChip", rightBlock, out pauseChipLabel);
            Clickable(followChip, () => FollowClicked?.Invoke());
            Clickable(pauseChip, () => PauseClicked?.Invoke());
            rightBlock.gameObject.SetActive(false);
        }

        internal RectTransform PauseBanner => pauseBanner;
        internal Text PauseLabel => pauseLabel;
        internal Image[] PauseGlyph => pauseGlyph;
        internal RectTransform ResultBanner => resultBanner;
        internal Text ResultLabel => resultLabel;
        internal RectTransform RightBlock => rightBlock;
        internal RectTransform ExtractionChip => extractionChip;
        internal Text ExtractionLabel => extractionLabel;
        internal RectTransform FollowChip => followChip;
        internal Text FollowLabel => followLabel;
        internal RectTransform PauseChip => pauseChip;
        internal Text PauseChipLabel => pauseChipLabel;

        /// <summary>Raised when the follow chip is clicked.</summary>
        internal event Action FollowClicked;
        /// <summary>Raised when the pause chip is clicked.</summary>
        internal event Action PauseClicked;

        public override void Apply(HudSnapshot s)
        {
            ApplyPause(s);
            ApplyResult(s);
            ApplyRight(s);
        }

        void ApplyPause(HudSnapshot s)
        {
            HudFactory.SetActive(pauseBanner.gameObject, s.IsPaused);
            if (!s.IsPaused)
                return;
            if (shownResume != null && string.Equals(shownResume, s.ResumePrompt, StringComparison.Ordinal))
                return;
            shownResume = s.ResumePrompt ?? string.Empty;
            HudFactory.SetText(pauseLabel, HudText.Pause(shownResume));
        }

        void ApplyResult(HudSnapshot s)
        {
            var visible = !string.IsNullOrEmpty(s.BannerText);
            HudFactory.SetActive(resultBanner.gameObject, visible);
            if (!visible || string.Equals(shownBanner, s.BannerText, StringComparison.Ordinal))
                return;
            shownBanner = s.BannerText;
            HudFactory.SetText(resultLabel, shownBanner);
            HudFactory.SetColor(resultLabel, string.Equals(shownBanner, FailedBanner, StringComparison.Ordinal) ? HudTheme.Bad : HudTheme.Good);
        }

        void ApplyRight(HudSnapshot s)
        {
            var showExtraction = s.Extraction != HudExtractionState.Hidden;
            HudFactory.SetActive(rightBlock.gameObject, showExtraction || s.HasFollow || s.HasPause);
            HudFactory.SetActive(extractionChip.gameObject, showExtraction);
            HudFactory.SetActive(followChip.gameObject, s.HasFollow);
            HudFactory.SetActive(pauseChip.gameObject, s.HasPause);

            if (showExtraction && (!hasExtraction || shownExtraction != s.Extraction
                || shownInside != s.ExtractionInside || shownRequired != s.ExtractionRequired))
            {
                hasExtraction = true;
                shownExtraction = s.Extraction;
                shownInside = s.ExtractionInside;
                shownRequired = s.ExtractionRequired;
                HudFactory.SetText(extractionLabel, HudText.ExtractionLabel(shownExtraction, shownInside, shownRequired));
                HudFactory.SetColor(extractionLabel, ExtractionColor(shownExtraction));
            }

            if (s.HasFollow)
            {
                HudFactory.SetText(followLabel, HudText.Follow(s.FollowOn));
                HudFactory.SetColor(followLabel, s.FollowOn ? HudTheme.Accent : HudTheme.TextDim);
            }

            if (s.HasPause)
            {
                HudFactory.SetText(pauseChipLabel, HudText.PauseChip(s.IsPaused));
                HudFactory.SetColor(pauseChipLabel, s.IsPaused ? HudTheme.Warn : HudTheme.Text);
            }
        }

        static Color ExtractionColor(HudExtractionState state)
        {
            switch (state)
            {
                case HudExtractionState.Available:
                case HudExtractionState.Active: return HudTheme.Accent;
                case HudExtractionState.Extracted: return HudTheme.Good;
                case HudExtractionState.Unknown: return HudTheme.Warn;
                default: return HudTheme.TextDim;
            }
        }

        static Image GlyphBar(string name, RectTransform glyph, float side)
        {
            var bar = HudFactory.Box(name, glyph, HudTheme.Warn, false);
            HudFactory.Anchor(bar, new Vector2(side, 0f), new Vector2(side, 1f), new Vector2(side, 0.5f), Vector2.zero, Vector2.zero);
            bar.sizeDelta = new Vector2(5f, 0f);
            return bar.GetComponent<Image>();
        }

        // A chip that takes pointer clicks: its background catches raycasts and a Button with no navigation raises `onClick`.
        static void Clickable(RectTransform chip, UnityEngine.Events.UnityAction onClick)
        {
            var background = chip.GetComponent<Image>();
            background.raycastTarget = true;
            var button = chip.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(onClick);
        }

        static RectTransform Chip(string name, RectTransform parent, out Text label)
        {
            var chip = HudFactory.Box(name, parent, HudTheme.Panel, false);
            HudFactory.Stack(chip, false, new RectOffset(12, 12, 8, 8), 0f, TextAnchor.MiddleRight);
            label = HudFactory.Label("Label", chip, HudTheme.FontBody, HudTheme.Text, TextAnchor.MiddleRight);
            HudFactory.Size(label, -1f, -1f, 1f);
            return chip;
        }
    }
}
