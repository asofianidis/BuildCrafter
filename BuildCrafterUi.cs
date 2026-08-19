using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace NevarisBuildCrafter;

internal sealed class BuildCrafterUi
{
    internal static readonly Color Backdrop = new(0.015f, 0.021f, 0.028f, 0.8f);
    internal static readonly Color Surface = new(0.035f, 0.047f, 0.059f, 0.99f);
    internal static readonly Color SurfaceLow = new(0.025f, 0.035f, 0.045f, 1f);
    internal static readonly Color SurfaceRaised = new(0.061f, 0.077f, 0.09f, 1f);
    internal static readonly Color SurfaceHover = new(0.095f, 0.116f, 0.13f, 1f);
    internal static readonly Color Accent = new(0.82f, 0.66f, 0.35f, 1f);
    internal static readonly Color AccentMuted = new(0.26f, 0.225f, 0.15f, 1f);
    internal static readonly Color Text = new(0.89f, 0.91f, 0.90f, 1f);
    internal static readonly Color TextStrong = new(0.96f, 0.965f, 0.95f, 1f);
    internal static readonly Color TextMuted = new(0.57f, 0.63f, 0.64f, 1f);
    internal static readonly Color Divider = new(0.15f, 0.18f, 0.195f, 1f);
    internal static readonly Color Positive = new(0.42f, 0.72f, 0.56f, 1f);
    internal static readonly Color Warning = new(0.86f, 0.57f, 0.36f, 1f);

    private readonly TMP_FontAsset _font;

    internal BuildCrafterUi()
    {
        try
        {
            _font = App.UI?.gameObject.GetComponentsInChildren<TMP_Text>(true)
                .FirstOrDefault(text => text is not null && text.font is not null)?.font;
        }
        catch { }
    }

    internal GameObject Rect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
        Vector2 offsetMin, Vector2 offsetMax)
    {
        GameObject obj = new(name);
        obj.transform.SetParent(parent, false);
        RectTransform rect = obj.AddComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
        return obj;
    }

    internal Image Panel(GameObject obj, Color color)
    {
        Image image = obj.AddComponent<Image>();
        image.color = color;
        return image;
    }

    internal TextMeshProUGUI TextBlock(string name, Transform parent, string value, float size,
        Color color, TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft,
        FontStyles style = FontStyles.Normal, bool richText = false)
    {
        GameObject obj = Rect(name, parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        TextMeshProUGUI text = obj.AddComponent<TextMeshProUGUI>();
        if (_font is not null)
            text.font = _font;
        text.text = value ?? string.Empty;
        text.fontSize = size;
        text.color = color;
        text.alignment = alignment;
        text.fontStyle = style;
        text.richText = richText;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        return text;
    }

    internal Button Button(string name, Transform parent, string label, Action onClick,
        bool selected = false, bool primary = false, float fontSize = 14f)
    {
        GameObject obj = Rect(name, parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        Color normal = selected || primary ? AccentMuted : SurfaceRaised;
        // Selectable tint colors multiply the target graphic color. Keep the graphic white so
        // the palette below is rendered once instead of squaring dark surface values into black.
        Image image = Panel(obj, Color.white);
        Button button = obj.AddComponent<Button>();
        button.targetGraphic = image;
        ColorBlock colors = ColorBlock.defaultColorBlock;
        colors.normalColor = normal;
        colors.highlightedColor = selected || primary ? new Color(0.33f, 0.28f, 0.18f, 1f) : SurfaceHover;
        colors.pressedColor = AccentMuted;
        colors.selectedColor = normal;
        colors.disabledColor = new Color(SurfaceRaised.r, SurfaceRaised.g, SurfaceRaised.b, 0.42f);
        colors.colorMultiplier = 1f;
        button.colors = colors;
        TextMeshProUGUI text = TextBlock("Label", obj.transform, label, fontSize,
            selected || primary ? Accent : Text, TextAlignmentOptions.Center,
            selected || primary ? FontStyles.Bold : FontStyles.Normal);
        text.rectTransform.offsetMin = new Vector2(8f, 3f);
        text.rectTransform.offsetMax = new Vector2(-8f, -3f);
        Wire(button, onClick);
        return button;
    }

    internal TMP_InputField Input(string name, Transform parent, string placeholder,
        Action<string> onChanged, string initial = "", bool multiline = false, int characterLimit = 100)
    {
        GameObject obj = Rect(name, parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        Image image = Panel(obj, SurfaceRaised);
        TMP_InputField input = obj.AddComponent<TMP_InputField>();

        TextMeshProUGUI placeholderText = TextBlock("Placeholder", obj.transform, placeholder,
            14f, TextMuted, multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft);
        placeholderText.rectTransform.offsetMin = new Vector2(12f, 6f);
        placeholderText.rectTransform.offsetMax = new Vector2(-12f, -6f);
        placeholderText.fontStyle = FontStyles.Italic;

        TextMeshProUGUI valueText = TextBlock("Value", obj.transform, initial,
            14f, Text, multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft);
        valueText.rectTransform.offsetMin = new Vector2(12f, 6f);
        valueText.rectTransform.offsetMax = new Vector2(-12f, -6f);

        input.targetGraphic = image;
        input.textViewport = obj.GetComponent<RectTransform>();
        input.textComponent = valueText;
        input.placeholder = placeholderText;
        input.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
        input.characterLimit = characterLimit;
        input.SetTextWithoutNotify(initial ?? string.Empty);
        input.onValueChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<string>>(
            new Action<string>(value => onChanged?.Invoke(value ?? string.Empty))));
        return input;
    }

    internal ScrollRect Scroll(string name, Transform parent, out Transform content,
        float spacing = 5f, bool useLayout = true)
    {
        GameObject scrollObject = Rect(name, parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        ScrollRect scroll = scrollObject.AddComponent<ScrollRect>();
        GameObject viewport = Rect("Viewport", scrollObject.transform, Vector2.zero, Vector2.one,
            Vector2.zero, Vector2.zero);
        Panel(viewport, new Color(0f, 0f, 0f, 0.001f));
        viewport.AddComponent<RectMask2D>();
        GameObject contentObject = Rect("Content", viewport.transform,
            new Vector2(0f, 1f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);
        RectTransform contentRect = contentObject.GetComponent<RectTransform>();
        contentRect.pivot = new Vector2(0.5f, 1f);
        if (useLayout)
        {
            VerticalLayoutGroup layout = contentObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            ContentSizeFitter fitter = contentObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }
        scroll.viewport = viewport.GetComponent<RectTransform>();
        scroll.content = contentRect;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.scrollSensitivity = 34f;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        content = contentObject.transform;
        return scroll;
    }

    internal GameObject DividerLine(string name, Transform parent)
    {
        GameObject line = Rect(name, parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        Panel(line, Divider);
        return line;
    }

    internal static void AddLayout(GameObject obj, float height)
    {
        LayoutElement layout = obj.GetComponent<LayoutElement>() ?? obj.AddComponent<LayoutElement>();
        layout.minHeight = height;
        layout.preferredHeight = height;
        layout.flexibleHeight = 0f;
        layout.flexibleWidth = 1f;
    }

    internal static void Wire(Button button, Action onClick)
    {
        button.onClick.RemoveAllListeners();
        if (onClick is not null)
            button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(onClick));
    }

    internal static void Clear(Transform parent)
    {
        if (parent is null)
            return;
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            GameObject child = parent.GetChild(i).gameObject;
            child.SetActive(false);
            UnityEngine.Object.Destroy(child);
        }
    }
}
