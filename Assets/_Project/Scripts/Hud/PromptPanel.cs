using UnityEngine;
using UnityEngine.UI;

namespace Blackglass
{
    /// <summary>
    /// Bottom right (380 x 220): the contextual control hints, one row per snapshot entry, at most six. Each row is a boxed
    /// chip with the prompt text and the label beside it. The text is whatever the snapshot carries (it is resolved per
    /// input family upstream), so a controller switch changes the chips and nothing here names a key or button. The rows
    /// are pooled, rest on the bottom of the zone (row 0 on top) and are hidden and blanked when unused (a hidden row never
    /// keeps an old terminal name); with no entries nothing is drawn. Nothing in it is a pointer target.
    /// </summary>
    internal sealed class PromptPanel : HudPanel
    {
        public const int MaxRows = 6;
        public const float Width = 380f;
        public const float Height = 220f;
        public const float MinChipWidth = 44f;
        public const float MaxChipWidth = 250f;
        const float Pad = 8f;
        const float RowHeight = 30f;
        const float ChipHeight = 26f;
        const float RowGap = 4f;
        const float ChipGap = 10f;
        const float ChipInset = 6f;
        const float ChipSlack = 2f;
        public static readonly Vector2 Size = new Vector2(Width, Height);

        readonly RectTransform background;
        readonly RectTransform[] rows = new RectTransform[MaxRows];
        readonly RectTransform[] chipBoxes = new RectTransform[MaxRows];
        readonly Text[] chips = new Text[MaxRows];
        readonly Text[] labels = new Text[MaxRows];
        int shownCount = -1;
        float shownChipWidth = -1f;

        public PromptPanel(Transform parent) : base(HudFactory.Rect("Prompts", parent))
        {
            HudFactory.Place(Root, new Vector2(1f, 0f), new Vector2(-HudTheme.Margin, HudTheme.Margin), Size);
            background = HudFactory.Box("Background", Root, HudTheme.Panel, false);
            background.gameObject.SetActive(false);
            for (var i = 0; i < MaxRows; i++)
            {
                var row = HudFactory.Rect("Row" + i, Root);
                HudFactory.Place(row, new Vector2(0f, 0f), Vector2.zero, new Vector2(Width, RowHeight));
                chipBoxes[i] = HudFactory.Box("Chip", row, HudTheme.PanelEdge, false);
                chips[i] = HudFactory.Label("Prompt", chipBoxes[i], HudTheme.FontBody, HudTheme.Accent, TextAnchor.MiddleCenter);
                chips[i].verticalOverflow = VerticalWrapMode.Truncate;
                HudFactory.Anchor(chips[i].rectTransform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
                    new Vector2(ChipInset, 0f), new Vector2(-ChipInset, 0f));
                labels[i] = HudFactory.Label("Label", row, HudTheme.FontBody, HudTheme.Text, TextAnchor.MiddleLeft);
                labels[i].verticalOverflow = VerticalWrapMode.Truncate;   // a long label never spills into the next row
                rows[i] = row;
                row.gameObject.SetActive(false);
            }
            ArrangeColumns(MinChipWidth);
        }

        internal int RowCapacity => MaxRows;
        internal RectTransform Background => background;
        internal GameObject Row(int i) => rows[i].gameObject;
        internal RectTransform ChipBox(int i) => chipBoxes[i];
        internal Text Chip(int i) => chips[i];
        internal Text Label(int i) => labels[i];

        public override void Apply(HudSnapshot s)
        {
            var count = Mathf.Min(s.Prompts.Count, MaxRows);
            var countChanged = count != shownCount;
            if (countChanged)
            {
                shownCount = count;
                Arrange(count);
            }
            var writes = HudFactory.TextWrites;
            for (var i = 0; i < MaxRows; i++)
            {
                var visible = i < count;
                HudFactory.SetActive(rows[i].gameObject, visible);
                if (!visible)
                {
                    HudFactory.SetText(chips[i], string.Empty);
                    HudFactory.SetText(labels[i], string.Empty);
                    continue;
                }
                var entry = s.Prompts[i];
                HudFactory.SetText(chips[i], entry.Prompt);
                HudFactory.SetText(labels[i], entry.Label);
            }
            if (countChanged || writes != HudFactory.TextWrites)
                FitChipColumn(count);
        }

        /// <summary>
        /// The chips share one width: the widest shown prompt plus its padding (so a long chord never wraps), kept between a
        /// floor and a cap; the labels take the rest of the row. Runs only after a text or the row count changed.
        /// </summary>
        void FitChipColumn(int count)
        {
            var widest = 0f;
            for (var i = 0; i < count; i++)
                widest = Mathf.Max(widest, chips[i].preferredWidth);
            var width = Mathf.Clamp(Mathf.Ceil(widest) + 2f * ChipInset + ChipSlack, MinChipWidth, MaxChipWidth);
            if (!Mathf.Approximately(width, shownChipWidth))
                ArrangeColumns(width);
        }

        void ArrangeColumns(float chipWidth)
        {
            shownChipWidth = chipWidth;
            var labelX = Pad + chipWidth + ChipGap;
            for (var i = 0; i < MaxRows; i++)
            {
                HudFactory.Place(chipBoxes[i], new Vector2(0f, 0.5f), new Vector2(Pad, 0f), new Vector2(chipWidth, ChipHeight));
                HudFactory.Place(labels[i].rectTransform, new Vector2(0f, 0.5f), new Vector2(labelX, 0f),
                    new Vector2(Width - labelX - Pad, RowHeight));
            }
        }

        /// <summary>Row i sits (count - 1 - i) rows above the zone bottom, so row 0 is the top one; the box wraps the shown rows.</summary>
        void Arrange(int count)
        {
            HudFactory.SetActive(background.gameObject, count > 0);
            if (count > 0)
            {
                var height = count * RowHeight + (count - 1) * RowGap + 2f * Pad;
                HudFactory.Place(background, new Vector2(0f, 0f), Vector2.zero, new Vector2(Width, height));
            }
            for (var i = 0; i < MaxRows; i++)
            {
                var rowsAbove = Mathf.Max(count - 1 - i, 0);
                HudFactory.Place(rows[i], new Vector2(0f, 0f), new Vector2(0f, Pad + rowsAbove * (RowHeight + RowGap)),
                    new Vector2(Width, RowHeight));
            }
        }
    }
}
