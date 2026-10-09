using UnityEngine;
using UnityEngine.UI;

namespace Blackglass
{
    /// <summary>
    /// Builds the tactical HUD's uGUI pieces from code (no prefabs, no assets) and writes to them only on change. Text goes
    /// through this one class, so a later switch to another text component touches only here. Every Graphic it makes
    /// ignores pointer raycasts unless asked otherwise.
    /// </summary>
    public static class HudFactory
    {
        static Font font;
        static Sprite white;

        /// <summary>The engine's built-in legacy font (falls back to an OS font if the build has none).</summary>
        public static Font Font
        {
            get
            {
                if (font == null)
                {
                    font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    if (font == null)
                        font = Font.CreateDynamicFontFromOSFont("Arial", HudTheme.FontBody);
                }
                return font;
            }
        }

        /// <summary>A 1x1 white sprite, created once; filled images need a sprite to fill.</summary>
        static Sprite White
        {
            get
            {
                if (white == null)
                {
                    var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "HudWhite", hideFlags = HideFlags.HideAndDontSave };
                    texture.SetPixel(0, 0, Color.white);
                    texture.Apply(false, true);
                    white = Sprite.Create(texture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 100f);
                    white.name = "HudWhite";
                    white.hideFlags = HideFlags.HideAndDontSave;
                }
                return white;
            }
        }

        /// <summary>How many times SetText actually assigned a text (tests: a steady HUD writes nothing).</summary>
        internal static int TextWrites { get; private set; }

        /// <summary>A RectTransform with no graphic, stretched over its parent until anchored otherwise.</summary>
        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            if (parent != null)
                go.layer = parent.gameObject.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            Anchor(rect, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            return rect;
        }

        /// <summary>A solid box. It catches pointer raycasts only when `blocksWorld` (interactive or blocking backgrounds).</summary>
        public static RectTransform Box(string name, Transform parent, Color color, bool blocksWorld)
        {
            var rect = Rect(name, parent);
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = White;
            image.color = color;
            image.raycastTarget = blocksWorld;
            return rect;
        }

        /// <summary>A text label: wraps horizontally, never best-fits, no rich text, ignores raycasts.</summary>
        public static Text Label(string name, Transform parent, int size, Color color, TextAnchor anchor)
        {
            var rect = Rect(name, parent);
            var text = rect.gameObject.AddComponent<Text>();
            text.font = Font;
            text.fontSize = size;
            text.color = color;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.resizeTextForBestFit = false;
            text.supportRichText = false;
            text.raycastTarget = false;
            text.text = string.Empty;
            return text;
        }

        public static void Anchor(RectTransform r, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 offsetMin, Vector2 offsetMax)
        {
            r.anchorMin = anchorMin;
            r.anchorMax = anchorMax;
            r.pivot = pivot;
            r.offsetMin = offsetMin;
            r.offsetMax = offsetMax;
        }

        /// <summary>Pins `r` by one point: anchor and pivot at `anchor`, offset by `position`, sized `size`.</summary>
        public static void Place(RectTransform r, Vector2 anchor, Vector2 position, Vector2 size)
        {
            r.anchorMin = anchor;
            r.anchorMax = anchor;
            r.pivot = anchor;
            r.sizeDelta = size;
            r.anchoredPosition = position;
        }

        /// <summary>A bar: a `back` box with a horizontally filled `fill` image stretched over it (fillAmount 1).</summary>
        public static Image Bar(string name, Transform parent, Color back, Color fill, out Image fillImage)
        {
            var rect = Box(name, parent, back, false);
            var fillRect = Box("Fill", rect, fill, false);
            fillImage = fillRect.GetComponent<Image>();
            fillImage.type = Image.Type.Filled;
            fillImage.fillMethod = Image.FillMethod.Horizontal;
            fillImage.fillOrigin = (int)Image.OriginHorizontal.Left;
            fillImage.fillAmount = 1f;
            return rect.GetComponent<Image>();
        }

        /// <summary>Assigns the text only when it differs (ordinal compare), so an unchanged frame rebuilds no mesh.</summary>
        public static void SetText(Text label, string value)
        {
            if (value == null)
                value = string.Empty;
            if (string.Equals(label.text, value, System.StringComparison.Ordinal))
                return;
            label.text = value;
            TextWrites++;
        }

        public static void SetActive(GameObject go, bool active)
        {
            if (go.activeSelf != active)
                go.SetActive(active);
        }

        public static void SetColor(Graphic graphic, Color color)
        {
            if (graphic.color != color)
                graphic.color = color;
        }

        public static void SetFill(Image image, float amount)
        {
            amount = Mathf.Clamp01(amount);
            if (!Mathf.Approximately(image.fillAmount, amount))
                image.fillAmount = amount;
        }

        /// <summary>A vertical or horizontal layout group that sizes its children to their preferred size, no stretching.</summary>
        public static HorizontalOrVerticalLayoutGroup Stack(RectTransform r, bool vertical, RectOffset padding, float spacing, TextAnchor alignment)
        {
            HorizontalOrVerticalLayoutGroup group = vertical
                ? r.gameObject.AddComponent<VerticalLayoutGroup>()
                : (HorizontalOrVerticalLayoutGroup)r.gameObject.AddComponent<HorizontalLayoutGroup>();
            group.padding = padding;
            group.spacing = spacing;
            group.childAlignment = alignment;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            return group;
        }

        /// <summary>Sizes `r` to its content's preferred size on the chosen axes (only on a rect whose parent has no layout).</summary>
        public static void FitContent(RectTransform r, bool horizontal, bool vertical)
        {
            var fitter = r.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = horizontal ? ContentSizeFitter.FitMode.PreferredSize : ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = vertical ? ContentSizeFitter.FitMode.PreferredSize : ContentSizeFitter.FitMode.Unconstrained;
        }

        /// <summary>A fixed or flexible size inside a layout group (negative values leave that axis to the content).</summary>
        public static LayoutElement Size(Component c, float width, float height, float flexibleWidth = -1f)
        {
            var element = c.gameObject.AddComponent<LayoutElement>();
            if (width >= 0f) { element.minWidth = width; element.preferredWidth = width; }
            if (height >= 0f) { element.minHeight = height; element.preferredHeight = height; }
            element.flexibleWidth = flexibleWidth;
            return element;
        }
    }
}
