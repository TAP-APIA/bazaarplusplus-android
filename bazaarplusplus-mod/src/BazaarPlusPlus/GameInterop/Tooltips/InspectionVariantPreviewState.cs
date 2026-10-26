#nullable enable
using BazaarGameClient.Domain.Models.Cards;
using BazaarGameShared.Domain.Cards.Item;
using BazaarGameShared.Domain.Core.Types;
using TheBazaar.Tooltips;

namespace BazaarPlusPlus.GameInterop.Tooltips;

/// <summary>
/// Owns the tier/enchantment selection of a locked inspection tooltip and produces the preview
/// data for it.
///
/// The native lock flow uses <c>CardLockPreviewState</c> for the same job, but that type is only
/// reachable from <c>CardTooltipController.SetupLockVariantPanel</c>, whose eligibility rewrite
/// cannot be installed on IL2CPP thunks. The transformation below is the one the native state
/// performs: clone the catalog card, swap the tier attributes through
/// <c>CardExtensions.BuildAttributeDictionaryForTier</c>, then layer the enchantment through
/// <c>CardExtensions.ApplyPreviewEnchantment</c>.
/// </summary>
internal sealed class InspectionVariantPreviewState
{
    private readonly CardTooltipData _baseData;
    private readonly Card _baseCard;
    private readonly EEnchantmentType? _baseEnchantment;
    private CardTooltipData? _preview;

    public InspectionVariantPreviewState(CardTooltipData baseData, InspectionVariantOptions options)
    {
        _baseData = baseData;
        _baseCard = baseData.CardInstance;
        _baseEnchantment = BazaarPlusPlus.GameInterop.Cards.NativeCardOptionalFields.Enchantment(
            _baseCard
        );
        Options = options;
        SelectedTier = options.DefaultTier;
        SelectedEnchantment = options.DefaultEnchantment;
    }

    public InspectionVariantOptions Options { get; }

    public ETier SelectedTier { get; private set; }

    public EEnchantmentType? SelectedEnchantment { get; private set; }

    /// <summary>The tooltip data the inspection should currently display.</summary>
    public CardTooltipData Data => _preview ?? _baseData;

    /// <summary>False while the selection matches the card the catalog opened.</summary>
    public bool IsDefaultSelection =>
        SelectedTier == _baseCard.Tier && SelectedEnchantment == _baseEnchantment;

    public bool SelectTier(ETier tier)
    {
        if (SelectedTier == tier)
            return false;
        SelectedTier = tier;
        Rebuild();
        return true;
    }

    public bool SelectEnchantment(EEnchantmentType? enchantment)
    {
        if (SelectedEnchantment == enchantment)
            return false;
        SelectedEnchantment = enchantment;
        Rebuild();
        return true;
    }

    public bool Reset()
    {
        if (IsDefaultSelection)
            return false;
        SelectedTier = _baseCard.Tier;
        SelectedEnchantment = _baseEnchantment;
        _preview = null;
        return true;
    }

    public void Dispose() => _preview = null;

    private void Rebuild()
    {
        if (IsDefaultSelection)
        {
            _preview = null;
            return;
        }

        // BuildAttributeDictionaryForTier starts from the template's per-tier values, which is
        // exactly what the native preview does before applying an enchantment on top. It already
        // returns the projected dictionary the card and tooltip renderer expect.
        var attributes = TheBazaar.CardExtensions.BuildAttributeDictionaryForTier(
            _baseCard,
            _baseData.CardTemplate,
            SelectedTier
        );

        var previewCard = TheBazaar.CardExtensions.CreateCopy(_baseCard);
        previewCard.Tier = SelectedTier;
        previewCard.Template ??= _baseData.CardTemplate;

        if (
            previewCard.AsIl2Cpp<ItemCard>() is { } previewItem
            && _baseData.CardTemplate.TryCast<TCardItem>() is { } itemTemplate
        )
        {
            // Writes the enchantment's attribute overrides into the dictionary and sets
            // ItemCard.Enchantment, so the tooltip renders enchantment effects and colours.
            TheBazaar.CardExtensions.ApplyPreviewEnchantment(
                itemTemplate,
                previewItem,
                attributes,
                SelectedEnchantment.ToNativeNullable()
            );
        }

        previewCard.Attributes = attributes;
        _preview = new CardTooltipData(previewCard, _baseData.CardTemplate);
    }
}
