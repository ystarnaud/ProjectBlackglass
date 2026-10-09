using System;
using UnityEngine;
using UnityEngine.UI;

namespace Blackglass
{
    /// <summary>
    /// Top left: the mission phase line and one row per objective (the snapshot already applied the fog rules). Each row's
    /// state is a text marker as well as a colour, so it never relies on colour alone. Hidden without a mission. A row is
    /// blanked as it hides, so a hidden row never keeps a title (after a regeneration an objective is unknown again).
    /// </summary>
    internal sealed class ObjectivesPanel : HudPanel
    {
        public const int MaxRows = 8;
        public const float Width = 420f;
        const float MarkerWidth = 32f;
        const string HeaderPrefix = "MISSION  ";

        readonly Text header;
        readonly GameObject[] rows = new GameObject[MaxRows];
        readonly Text[] markers = new Text[MaxRows];
        readonly Text[] texts = new Text[MaxRows];
        readonly int[] shownKinds = new int[MaxRows];
        string shownPhase;

        public ObjectivesPanel(Transform parent) : base(HudFactory.Box("Objectives", parent, HudTheme.Panel, false))
        {
            HudFactory.Place(Root, new Vector2(0f, 1f), new Vector2(HudTheme.Margin, -HudTheme.Margin), new Vector2(Width, 0f));
            var stack = HudFactory.Stack(Root, true, new RectOffset(12, 12, 10, 12), 4f, TextAnchor.UpperLeft);
            stack.childForceExpandWidth = true;
            HudFactory.FitContent(Root, false, true);

            header = HudFactory.Label("Header", Root, HudTheme.FontBody, HudTheme.Accent, TextAnchor.MiddleLeft);
            for (var i = 0; i < MaxRows; i++)
            {
                var row = HudFactory.Rect("Row" + i, Root);
                HudFactory.Stack(row, false, new RectOffset(), 8f, TextAnchor.UpperLeft);
                markers[i] = HudFactory.Label("Marker", row, HudTheme.FontBody, HudTheme.Text, TextAnchor.UpperLeft);
                HudFactory.Size(markers[i], MarkerWidth, -1f);
                texts[i] = HudFactory.Label("Text", row, HudTheme.FontBody, HudTheme.Text, TextAnchor.UpperLeft);
                HudFactory.Size(texts[i], -1f, -1f, 1f);
                rows[i] = row.gameObject;
                rows[i].SetActive(false);
                shownKinds[i] = -1;
            }
        }

        internal Text Header => header;
        internal int RowCapacity => MaxRows;
        internal GameObject Row(int i) => rows[i];
        internal Text RowMarker(int i) => markers[i];
        internal Text RowText(int i) => texts[i];

        public override void Apply(HudSnapshot s)
        {
            HudFactory.SetActive(Root.gameObject, s.HasMission);
            if (!s.HasMission)
            {
                for (var i = 0; i < MaxRows; i++)
                    HudFactory.SetText(texts[i], string.Empty);
                return;
            }

            var phase = s.PhaseText ?? string.Empty;
            if (shownPhase == null || !string.Equals(shownPhase, phase, StringComparison.Ordinal))
            {
                shownPhase = phase;
                HudFactory.SetText(header, HeaderPrefix + shownPhase);
            }

            var count = Mathf.Min(s.Objectives.Count, MaxRows);
            for (var i = 0; i < MaxRows; i++)
            {
                var visible = i < count;
                HudFactory.SetActive(rows[i], visible);
                if (!visible)
                {
                    HudFactory.SetText(texts[i], string.Empty);
                    continue;
                }
                var row = s.Objectives[i];
                HudFactory.SetText(texts[i], row.Text);
                if (shownKinds[i] == (int)row.Kind)
                    continue;
                shownKinds[i] = (int)row.Kind;
                HudFactory.SetText(markers[i], Marker(row.Kind));
                HudFactory.SetColor(markers[i], MarkerColor(row.Kind));
                HudFactory.SetColor(texts[i], IsDimmed(row.Kind) ? HudTheme.TextDim : HudTheme.Text);
            }
        }

        static string Marker(HudObjectiveKind kind)
        {
            switch (kind)
            {
                case HudObjectiveKind.Completed: return "[x]";
                case HudObjectiveKind.Failed: return "[!]";
                case HudObjectiveKind.Locked: return "[-]";
                case HudObjectiveKind.Unknown: return "[?]";
                default: return "[ ]";
            }
        }

        static Color MarkerColor(HudObjectiveKind kind)
        {
            switch (kind)
            {
                case HudObjectiveKind.Completed: return HudTheme.Good;
                case HudObjectiveKind.Failed: return HudTheme.Bad;
                case HudObjectiveKind.Locked: return HudTheme.TextDim;
                case HudObjectiveKind.Unknown: return HudTheme.Warn;
                default: return HudTheme.Text;
            }
        }

        static bool IsDimmed(HudObjectiveKind kind) => kind == HudObjectiveKind.Completed || kind == HudObjectiveKind.Locked;
    }
}
