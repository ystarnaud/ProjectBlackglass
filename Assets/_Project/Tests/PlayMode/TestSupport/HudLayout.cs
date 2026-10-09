#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Blackglass.Tests
{
    /// <summary>
    /// Layout checks for the code-built HUD: a plain RectTransform stands in for the canvas, the layout is forced, and
    /// rects are compared in world space (their GetWorldCorners).
    /// </summary>
    internal static class HudLayout
    {
        const float Tolerance = 0.5f;

        /// <summary>
        /// The canvas size, in reference units, of a screen of `screenW` x `screenH` pixels under the HUD's CanvasScaler
        /// (ScaleWithScreenSize, 1920x1080, match 0.5): scale s = 2^lerp(log2(w/1920), log2(h/1080), 0.5), size (w/s, h/s).
        /// </summary>
        public static Vector2 CanvasUnits(int screenW, int screenH)
        {
            var logWidth = Mathf.Log(screenW / 1920f, 2f);
            var logHeight = Mathf.Log(screenH / 1080f, 2f);
            var scale = Mathf.Pow(2f, Mathf.Lerp(logWidth, logHeight, 0.5f));
            return new Vector2(screenW / scale, screenH / scale);
        }

        /// <summary>An active plain RectTransform of the given size, centred at the origin, to build a HUD under.</summary>
        public static RectTransform CreateRoot(float width, float height)
        {
            var root = new GameObject("HudLayoutRoot", typeof(RectTransform)).GetComponent<RectTransform>();
            root.anchorMin = root.anchorMax = root.pivot = new Vector2(0.5f, 0.5f);
            root.sizeDelta = new Vector2(width, height);
            root.anchoredPosition = Vector2.zero;
            return root;
        }

        /// <summary>
        /// Runs the layout now. LayoutRebuilder only descends through rects that carry layout components, so every rect with a
        /// ContentSizeFitter or a layout group is rebuilt from itself.
        /// </summary>
        public static void Rebuild(RectTransform root)
        {
            Canvas.ForceUpdateCanvases();
            foreach (var rect in root.GetComponentsInChildren<RectTransform>(true))
            {
                if (rect.GetComponent<ContentSizeFitter>() != null || rect.GetComponent<LayoutGroup>() != null)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
            }
            Canvas.ForceUpdateCanvases();
        }

        /// <summary>
        /// Whether every character of the label's text is drawn inside its rect (wrapping, truncation and best fit as the
        /// label is set up), and the font size used (the best-fit size when the label shrinks to fit).
        /// </summary>
        public static bool DrawsWhole(Text label, out int fontSizeUsed)
        {
            var generator = new TextGenerator();
            generator.Populate(label.text, label.GetGenerationSettings(label.rectTransform.rect.size));
            fontSizeUsed = label.resizeTextForBestFit ? generator.fontSizeUsedForBestFit : label.fontSize;
            return generator.characterCountVisible >= label.text.Length;
        }

        public static Rect WorldRect(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
        }

        /// <summary>Fails if any two rects overlap by more than half a unit (shared edges are fine).</summary>
        public static void AssertNoOverlap(params RectTransform[] rects)
        {
            for (var i = 0; i < rects.Length; i++)
            {
                Assert.That(rects[i], Is.Not.Null, $"rect #{i} is missing");
                for (var j = i + 1; j < rects.Length; j++)
                {
                    var a = WorldRect(rects[i]);
                    var b = WorldRect(rects[j]);
                    var overlaps = a.xMin < b.xMax - Tolerance && b.xMin < a.xMax - Tolerance
                        && a.yMin < b.yMax - Tolerance && b.yMin < a.yMax - Tolerance;
                    Assert.That(overlaps, Is.False, $"{rects[i].name} {a} overlaps {rects[j].name} {b}");
                }
            }
        }

        /// <summary>Fails unless every rect lies inside `parent` (within half a unit) and has a positive size.</summary>
        public static void AssertInside(RectTransform parent, params RectTransform[] rects)
        {
            var outer = WorldRect(parent);
            foreach (var rect in rects)
            {
                Assert.That(rect, Is.Not.Null, "rect is missing");
                var inner = WorldRect(rect);
                Assert.That(inner.width > 0f && inner.height > 0f, Is.True, $"{rect.name} has no size: {inner}");
                var inside = inner.xMin >= outer.xMin - Tolerance && inner.xMax <= outer.xMax + Tolerance
                    && inner.yMin >= outer.yMin - Tolerance && inner.yMax <= outer.yMax + Tolerance;
                Assert.That(inside, Is.True, $"{rect.name} {inner} is not inside {parent.name} {outer}");
            }
        }
    }
}
#endif
