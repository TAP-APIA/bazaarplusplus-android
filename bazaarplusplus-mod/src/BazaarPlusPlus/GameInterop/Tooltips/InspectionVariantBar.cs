#nullable enable
using BazaarGameShared.Domain.Core.Types;
using BazaarPlusPlus.GameInterop.Fonts;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.UiTokens;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BazaarPlusPlus.GameInterop.Tooltips;

/// <summary>
/// Touch-first quality/enchantment selector for the card inspection overlay.
///
/// The desktop client exposes the same axes through the native <c>CardLockVariantPanel</c>
/// dropdowns. Those dropdowns are pointer-driven and their panel is only created by
/// <c>CardTooltipController.SetupLockVariantPanel</c>, which relies on an eligibility rewrite that
/// cannot run on IL2CPP. This bar is owned by BPP instead: flat chips per quality, one chip row per
/// enchantment, a reset chip, and a nested-tooltip note, all sized for a fingertip.
///
/// Layout is intentionally a single horizontal band so it can be placed by the inspection module
/// under the description without competing with the native tooltip's own size fitters.
/// </summary>
internal sealed class InspectionVariantBar : IDisposable
{
    private const float ChipHeight = 58f;
    private const float ChipPadding = 26f;
    private const float ChipGap = 10f;
    private const float RowGap = 8f;
    private const float FontSize = 26f;
    private const float PanelPadding = 12f;

    private readonly RectTransform _root;
    private readonly RectTransform _tierRow;
    private readonly RectTransform _enchantRow;
    private readonly CanvasGroup _group;
    private readonly List<Chip> _tierChips = new();
    private readonly List<Chip> _enchantChips = new();
    private readonly Chip _resetChip;
    private FontScope? _font;
    private bool _disposed;

    public InspectionVariantBar(Transform parent, InspectionVariantOptions options)
    {
        _root = CreateRect("InspectionVariants", parent);
        _root.anchorMin = _root.anchorMax = new Vector2(.5f, 1f);
        _root.pivot = new Vector2(.5f, 1f);
        // A RectTransform that only sets anchors and pivot keeps a zero-sized rect, and a layout
        // group cannot grow a zero-width parent: the whole band collapsed to 0x0 and never showed
        // up on screen. Give the band a real width and a content-fitted height before layout runs.
        _root.sizeDelta = new Vector2(VariantBarWidth, 0f);

        var fitter = _root.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

        var outer = _root.gameObject.AddComponent<VerticalLayoutGroup>();
        outer.padding = new RectOffset(
            (int)PanelPadding,
            (int)PanelPadding,
            (int)PanelPadding,
            (int)PanelPadding
        );
        outer.spacing = RowGap;
        outer.childAlignment = TextAnchor.MiddleCenter;
        outer.childControlWidth = true;
        outer.childControlHeight = true;
        outer.childForceExpandWidth = true;
        outer.childForceExpandHeight = false;

        _group = _root.gameObject.AddComponent<CanvasGroup>();
        _group.alpha = 1f;
        _group.interactable = true;
        _group.blocksRaycasts = true;

        var backdrop = _root.gameObject.AddComponent<Image>();
        backdrop.sprite = RoundedSprite.Shared;
        backdrop.type = Image.Type.Sliced;
        backdrop.pixelsPerUnitMultiplier = 2f;
        backdrop.color = new Color(0.05f, 0.055f, 0.065f, 0.92f);
        backdrop.raycastTarget = true;

        _font = TryPrepareFont();
        _tierRow = CreateRow(_root, "Tiers");
        _enchantRow = CreateRow(_root, "Enchantments");
        var footer = CreateRow(_root, "Footer");
        _resetChip = CreateChip(
            footer,
            Game.CollectionPanel.CollectionPanelText.VariantReset(),
            OnReset,
            accent: Colors.ButtonSelectedBackground
        );
    }

    /// <summary>
    /// Width of the selector band in screen pixels. Clamped so the chips stay tappable on a phone
    /// and never stretch past the inspection content on a tablet.
    /// </summary>
    private static float VariantBarWidth => Mathf.Clamp(Screen.width * .62f, 520f, 1400f);

    /// <summary>Raised when the user picks a quality chip.</summary>
    public event Action<ETier>? TierSelected;

    /// <summary>Raised when the user picks an enchantment chip; <c>null</c> clears it.</summary>
    public event Action<EEnchantmentType?>? EnchantmentSelected;

    /// <summary>Raised when the user resets to the catalog card's own quality/enchantment.</summary>
    public event Action? ResetRequested;

    public RectTransform Root => _root;

    public bool Visible
    {
        get => _root.gameObject.activeSelf;
        set => _root.gameObject.SetActive(value);
    }

    /// <summary>Builds chips for the options and the currently selected pair.</summary>
    public void Populate(
        InspectionVariantOptions options,
        ETier selectedTier,
        EEnchantmentType? selectedEnchantment
    )
    {
        PopulateTiers(options, selectedTier);
        PopulateEnchantments(options, selectedEnchantment);
        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(_root);
    }

    /// <summary>Highlights the active chips without rebuilding the row.</summary>
    public void SyncSelection(ETier selectedTier, EEnchantmentType? selectedEnchantment)
    {
        foreach (var chip in _tierChips)
            chip.SetActive(chip.Tier == selectedTier);
        foreach (var chip in _enchantChips)
            chip.SetActive(chip.Enchantment == selectedEnchantment);
    }

    /// <summary>Measured size of the bar in screen pixels.</summary>
    public Vector2 Measure()
    {
        LayoutRebuilder.ForceRebuildLayoutImmediate(_root);
        return _root.rect.size;
    }

    internal void FitWidth(float availablePixels)
    {
        var width = Mathf.Max(1f, Mathf.Min(VariantBarWidth, availablePixels));
        if (!Mathf.Approximately(_root.sizeDelta.x, width))
            _root.sizeDelta = new Vector2(width, _root.sizeDelta.y);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_root != null)
            Object.Destroy(_root.gameObject);
        _font?.Dispose();
        _font = null;
    }

    private void PopulateTiers(InspectionVariantOptions options, ETier selectedTier)
    {
        Clear(_tierChips);
        var any = false;
        foreach (var option in options.Tiers)
        {
            var chip = CreateChip(
                _tierRow,
                option.Label,
                () => TierSelected?.Invoke(option.Tier),
                accent: TierAccent(option.Tier)
            );
            chip.Tier = option.Tier;
            chip.SetActive(option.Tier == selectedTier);
            _tierChips.Add(chip);
            any = true;
        }
        // A single quality is not a choice; hide the axis instead of showing one dead chip.
        _tierRow.gameObject.SetActive(any && options.HasMultipleTiers);
    }

    private void PopulateEnchantments(
        InspectionVariantOptions options,
        EEnchantmentType? selectedEnchantment
    )
    {
        Clear(_enchantChips);
        if (!options.HasMultipleEnchantments)
        {
            _enchantRow.gameObject.SetActive(false);
            return;
        }
        _enchantRow.gameObject.SetActive(true);
        foreach (var option in options.Enchantments)
        {
            var chip = CreateChip(
                _enchantRow,
                option.Label,
                () => EnchantmentSelected?.Invoke(option.EnchantmentType),
                accent: option.EnchantmentType is { } type
                    ? EnchantmentAccent(type)
                    : Colors.CollectionChipSelectedBorder
            );
            chip.Enchantment = option.EnchantmentType;
            chip.SetActive(option.EnchantmentType == selectedEnchantment);
            _enchantChips.Add(chip);
        }
    }

    private void OnReset()
    {
        ResetRequested?.Invoke();
    }

    private RectTransform CreateRow(Transform parent, string name)
    {
        var row = CreateRect(name, parent);
        row.sizeDelta = new Vector2(0f, ChipHeight);
        var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = ChipGap;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = true;
        var element = row.gameObject.AddComponent<LayoutElement>();
        element.minHeight = element.preferredHeight = ChipHeight;
        return row;
    }

    private Chip CreateChip(Transform parent, string label, Action click, Color accent)
    {
        var rect = CreateRect("Chip", parent);
        var element = rect.gameObject.AddComponent<LayoutElement>();
        element.minHeight = element.preferredHeight = ChipHeight;

        var background = rect.gameObject.AddComponent<Image>();
        background.sprite = RoundedSprite.Shared;
        background.type = Image.Type.Sliced;
        background.pixelsPerUnitMultiplier = 2f;
        background.color = Colors.CollectionChipBackground;
        background.raycastTarget = true;

        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = background;
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(BppDelegates.UnityAction(click));

        var textRect = CreateRect("Label", rect);
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(ChipPadding * .5f, 0f);
        textRect.offsetMax = new Vector2(-ChipPadding * .5f, 0f);
        var text = textRect.gameObject.AddComponent<TextMeshProUGUI>();
        _font?.Apply(text);
        text.text = label;
        text.richText = false;
        text.fontSize = FontSize;
        text.fontSizeMin = FontSize * .7f;
        text.fontSizeMax = FontSize;
        text.enableAutoSizing = true;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.color = Colors.CollectionChipText;
        text.raycastTarget = false;

        return new Chip(rect, background, text, accent);
    }

    private static void Clear(List<Chip> chips)
    {
        foreach (var chip in chips)
            chip.Dispose();
        chips.Clear();
    }

    private static Color TierAccent(ETier tier) =>
        ColorUtility.TryParseHtmlString(
            "#" + Game.CollectionPanel.Tooltips.CollectionTierTooltipTextMerger.TierColorHex(tier),
            out var color
        )
            ? color
            : Colors.CollectionChipSelectedBorder;

    private static Color EnchantmentAccent(EEnchantmentType type) =>
        ColorUtility.TryParseHtmlString(
            "#" + Game.ItemEnchantPreview.ItemEnchantPreviewFormatting.GetEnchantmentColorHex(type),
            out var color
        )
            ? color
            : Colors.CollectionChipSelectedBorder;

    private static RectTransform CreateRect(string name, Transform parent)
    {
        var rect = BppGameObjectFactory
            .Create(name, typeof(RectTransform))
            .GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.localScale = Vector3.one;
        return rect;
    }

    private static FontScope? TryPrepareFont()
    {
        try
        {
            var outcome = NativeGameTypography.PrepareOwnedText(out var preparation);
            return preparation != null ? new FontScope(preparation) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A chip's two visual states. The accent comes from the game's own tier/enchantment palette
    /// so a selected quality reads exactly like the card frame it previews.
    /// </summary>
    private sealed class Chip : IDisposable
    {
        private readonly RectTransform _rect;
        private readonly Image _background;
        private readonly TextMeshProUGUI _text;
        private readonly Color _accent;

        public Chip(RectTransform rect, Image background, TextMeshProUGUI text, Color accent)
        {
            _rect = rect;
            _background = background;
            _text = text;
            _accent = accent;
        }

        public ETier Tier { get; set; }

        public EEnchantmentType? Enchantment { get; set; }

        public void SetActive(bool active)
        {
            if (_background == null)
                return;
            _background.color = active
                ? Colors.WithAlpha(_accent, 0.85f)
                : Colors.CollectionChipBackground;
            _text.color = active ? Colors.HeroDarkText : Colors.CollectionChipText;
        }

        public void Dispose()
        {
            if (_rect != null)
                Object.Destroy(_rect.gameObject);
        }
    }

    private sealed class FontScope : IDisposable
    {
        private readonly NativeGameTypography.OwnedTextPreparation _preparation;

        public FontScope(NativeGameTypography.OwnedTextPreparation preparation) =>
            _preparation = preparation;

        public void Apply(TextMeshProUGUI text) => _preparation.Apply(text);

        public void Dispose() { }
    }
}

/// <summary>
/// Lazily built white rounded-rect sprite shared by inspection chips, so the selector needs no
/// game asset and stays crisp at any panel scale.
/// </summary>
internal static class RoundedSprite
{
    private const int Size = 32;
    private const int Radius = 12;
    private static Sprite? _shared;

    internal static Sprite? Shared
    {
        get
        {
            if (_shared != null)
                return _shared;
            var texture = new Texture2D(Size, Size, TextureFormat.ARGB32, false)
            {
                name = "BppInspectionRounded",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var clampedX = Mathf.Clamp(x, Radius, Size - Radius - 1);
                var clampedY = Mathf.Clamp(y, Radius, Size - Radius - 1);
                var dx = x - clampedX;
                var dy = y - clampedY;
                var inside = (dx * dx) + (dy * dy) <= Radius * Radius;
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, inside ? 1f : 0f));
            }
            texture.Apply();
            _shared = Sprite.Create(
                texture,
                new Rect(0f, 0f, Size, Size),
                new Vector2(.5f, .5f),
                100f,
                0u,
                SpriteMeshType.FullRect,
                new Vector4(Radius, Radius, Radius, Radius)
            );
            _shared.hideFlags = HideFlags.HideAndDontSave;
            return _shared;
        }
    }
}
