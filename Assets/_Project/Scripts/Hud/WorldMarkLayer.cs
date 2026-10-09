using System;
using UnityEngine;
using UnityEngine.UI;

namespace Blackglass
{
    /// <summary>
    /// Full-screen layer beneath every panel that draws the snapshot's world marks as screen markers. Each Apply projects
    /// every mark's world point into the root rect (camera.WorldToScreenPoint, then the overlay-canvas conversion, so the
    /// canvas scale is honoured, and the root's pivot does not matter); a mark behind the camera, or projected more than
    /// CullMargin units outside the root, is hidden. A hostile is a small filled diamond with no text, a
    /// last-known position a hollow diamond with "?" and its label, an objective or the extraction a small square with its
    /// label: shape and text differ, never colour alone. Entries are pooled (grown on demand up to Capacity, extra marks
    /// are dropped) and written only on change; a label that hides or loses its text is blanked, so a pooled entry never
    /// keeps an old title. Nothing here is a pointer target.
    /// </summary>
    internal sealed class WorldMarkLayer : HudPanel
    {
        public const int Capacity = 32;
        const float FilledSide = 9f;          // turned 45 degrees: about 13 units across
        const float HollowSide = 16f;         // turned 45 degrees: about 23 units across
        const float EdgeThickness = 2f;
        const float SquareSide = 10f;
        const float LabelOffset = 16f;
        const float LabelWidth = 200f;
        const float LabelHeight = 20f;
        const float MoveEpsilonSquared = 0.01f;   // 0.1 canvas units
        const float CullMargin = 200f;            // canvas units beyond the root's edges before a mark is hidden
        const string GlyphText = "?";

        /// <summary>One pooled marker: its root (moved to the projected point) and every shape and text a kind can show.</summary>
        internal sealed class MarkView
        {
            public RectTransform Root;
            public Image Filled;
            public RectTransform Hollow;
            public Image[] HollowBars;
            public Text Glyph;
            public Image Square;
            public Text Label;
            public HudMarkKind ShownKind;
            public bool HasKind;
            public bool HasPosition;
            public Vector2 ShownPosition;
        }

        readonly MarkView[] views = new MarkView[Capacity];
        int poolCount;
        int visibleCount;

        public WorldMarkLayer(Transform parent) : base(HudFactory.Rect("WorldMarks", parent)) { }

        /// <summary>The camera the marks are projected with (set by the HUD; tests assign their own). Null hides every mark.</summary>
        internal Func<Camera> CameraSource { get; set; }

        internal int PoolCount => poolCount;
        internal int VisibleCount => visibleCount;
        internal MarkView View(int index) => views[index];

        public override void Apply(HudSnapshot s)
        {
            var camera = CameraSource != null ? CameraSource() : null;
            var marks = s.Marks;
            var wanted = Mathf.Min(marks.Count, Capacity);
            var shown = 0;
            for (var i = 0; i < wanted; i++)
            {
                if (i == poolCount)
                {
                    views[i] = CreateView(i);
                    poolCount++;
                }
                var view = views[i];
                var mark = marks[i];
                if (!TryProject(camera, mark.World, out var local))
                {
                    Hide(view);
                    continue;
                }
                HudFactory.SetActive(view.Root.gameObject, true);
                shown++;
                ApplyKind(view, mark);
                if (!view.HasPosition || (local - view.ShownPosition).sqrMagnitude > MoveEpsilonSquared)
                {
                    view.HasPosition = true;
                    view.ShownPosition = local;
                    view.Root.anchoredPosition = local;
                }
            }
            for (var i = wanted; i < poolCount; i++)
                Hide(views[i]);
            visibleCount = shown;
        }

        bool TryProject(Camera camera, Vector3 world, out Vector2 local)
        {
            local = default;
            if (camera == null)
                return false;
            var screen = camera.WorldToScreenPoint(world);
            if (!(screen.z > 0f))   // behind the camera, on its plane, or NaN
                return false;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(Root, new Vector2(screen.x, screen.y), null, out var point))
                return false;
            // `point` is relative to the root's pivot; a mark is anchored at the root's centre.
            var rect = Root.rect;
            if (!(point.x >= rect.xMin - CullMargin && point.x <= rect.xMax + CullMargin
                  && point.y >= rect.yMin - CullMargin && point.y <= rect.yMax + CullMargin))
                return false;
            local = point - rect.center;
            return true;
        }

        static void Hide(MarkView view)
        {
            HudFactory.SetActive(view.Root.gameObject, false);
            HudFactory.SetText(view.Label, string.Empty);
            view.HasPosition = false;
        }

        static void ApplyKind(MarkView view, HudWorldMark mark)
        {
            if (!view.HasKind || view.ShownKind != mark.Kind)
            {
                view.HasKind = true;
                view.ShownKind = mark.Kind;
                var hostile = mark.Kind == HudMarkKind.Hostile;
                var lastKnown = mark.Kind == HudMarkKind.LastKnown;
                var tint = hostile ? HudTheme.Bad : lastKnown ? HudTheme.Warn : mark.Kind == HudMarkKind.Extraction ? HudTheme.Good : HudTheme.Accent;
                HudFactory.SetActive(view.Filled.gameObject, hostile);
                HudFactory.SetActive(view.Hollow.gameObject, lastKnown);
                HudFactory.SetActive(view.Glyph.gameObject, lastKnown);
                HudFactory.SetActive(view.Square.gameObject, !hostile && !lastKnown);
                HudFactory.SetColor(view.Filled, tint);
                HudFactory.SetColor(view.Square, tint);
                HudFactory.SetColor(view.Glyph, tint);
                HudFactory.SetColor(view.Label, tint);
                for (var i = 0; i < view.HollowBars.Length; i++)
                    HudFactory.SetColor(view.HollowBars[i], tint);
            }

            var text = mark.Text;
            var hasText = !string.IsNullOrEmpty(text);
            HudFactory.SetActive(view.Label.gameObject, hasText);
            HudFactory.SetText(view.Label, hasText ? text : string.Empty);
        }

        MarkView CreateView(int index)
        {
            var view = new MarkView { Root = HudFactory.Rect("Mark" + index, Root) };
            HudFactory.Place(view.Root, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

            var filled = HudFactory.Box("Filled", view.Root, HudTheme.Bad, false);
            Centre(filled, FilledSide);
            filled.localRotation = Quaternion.Euler(0f, 0f, 45f);
            view.Filled = filled.GetComponent<Image>();

            view.Hollow = HudFactory.Rect("Hollow", view.Root);
            Centre(view.Hollow, HollowSide);
            view.Hollow.localRotation = Quaternion.Euler(0f, 0f, 45f);
            view.HollowBars = new Image[4];
            var up = Vector2.up;
            var right = Vector2.right;
            var edge = EdgeThickness;
            for (var i = 0; i < 4; i++)
            {
                var bar = HudFactory.Box("Edge" + i, view.Hollow, HudTheme.Warn, false);
                switch (i)
                {
                    case 0: HudFactory.Anchor(bar, up, Vector2.one, new Vector2(0.5f, 1f), new Vector2(0f, -edge), Vector2.zero); break;          // top
                    case 1: HudFactory.Anchor(bar, Vector2.zero, right, new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, edge)); break;       // bottom
                    case 2: HudFactory.Anchor(bar, Vector2.zero, up, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(edge, 0f)); break;          // left
                    default: HudFactory.Anchor(bar, right, Vector2.one, new Vector2(1f, 0.5f), new Vector2(-edge, 0f), Vector2.zero); break;      // right
                }
                view.HollowBars[i] = bar.GetComponent<Image>();
            }

            view.Glyph = HudFactory.Label("Glyph", view.Root, HudTheme.FontSmall, HudTheme.Warn, TextAnchor.MiddleCenter);
            Centre(view.Glyph.rectTransform, HollowSide);
            view.Glyph.horizontalOverflow = HorizontalWrapMode.Overflow;
            view.Glyph.text = GlyphText;

            var square = HudFactory.Box("Square", view.Root, HudTheme.Accent, false);
            Centre(square, SquareSide);
            view.Square = square.GetComponent<Image>();

            view.Label = HudFactory.Label("Label", view.Root, HudTheme.FontSmall, HudTheme.Accent, TextAnchor.MiddleLeft);
            var labelRect = view.Label.rectTransform;
            HudFactory.Place(labelRect, new Vector2(0.5f, 0.5f), new Vector2(LabelOffset, 0f), new Vector2(LabelWidth, LabelHeight));
            labelRect.pivot = new Vector2(0f, 0.5f);   // the label grows rightwards from the offset
            view.Label.horizontalOverflow = HorizontalWrapMode.Overflow;

            view.Filled.gameObject.SetActive(false);
            view.Hollow.gameObject.SetActive(false);
            view.Glyph.gameObject.SetActive(false);
            view.Square.gameObject.SetActive(false);
            view.Label.gameObject.SetActive(false);
            view.Root.gameObject.SetActive(false);
            return view;
        }

        // A square of `side` centred on the parent's centre.
        static void Centre(RectTransform r, float side)
        {
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(side, side);
            r.anchoredPosition = Vector2.zero;
        }
    }
}
