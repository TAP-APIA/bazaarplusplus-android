#nullable enable
using BazaarGameClient.Domain.Models.Cards;
using BazaarGameShared.Domain.Cards.Interfaces;
using BazaarGameShared.Domain.Core.Types;
using BazaarPlusPlus.Game.CollectionPanel.Tooltips;
using BazaarPlusPlus.Game.ItemEnchantPreview;
using BazaarPlusPlus.Localization;
using TheBazaar.Tooltips;

namespace BazaarPlusPlus.GameInterop.Tooltips;

/// <summary>
/// One inspectable quality (tier) of the card currently locked in the inspection overlay.
/// </summary>
internal readonly struct InspectionTierChoice
{
    public InspectionTierChoice(ETier tier, string label, bool isBase)
    {
        Tier = tier;
        Label = label ?? string.Empty;
        IsBase = isBase;
    }

    public ETier Tier { get; }
    public string Label { get; }

    /// <summary>True for the tier the catalog card was opened at.</summary>
    public bool IsBase { get; }
}

/// <summary>
/// One inspectable enchantment of the card currently locked in the inspection overlay.
/// A <c>null</c> <see cref="EnchantmentType"/> is the "unenchanted" entry.
/// </summary>
internal readonly struct InspectionEnchantChoice
{
    public InspectionEnchantChoice(EEnchantmentType? enchantmentType, string label, bool isBase)
    {
        EnchantmentType = enchantmentType;
        Label = label ?? string.Empty;
        IsBase = isBase;
    }

    public EEnchantmentType? EnchantmentType { get; }
    public string Label { get; }

    /// <summary>True when the choice matches the enchantment the catalog card was opened with.</summary>
    public bool IsBase { get; }
}

/// <summary>
/// The quality/enchantment axes available for the locked inspection card.
///
/// This mirrors what the native <c>CardLockVariantOptions</c> offers on desktop, but it is built
/// from the same game data the tooltip itself renders, so Android does not depend on the managed
/// transpiler patch that cannot run against IL2CPP thunks.
/// </summary>
internal sealed class InspectionVariantOptions
{
    private InspectionVariantOptions(
        IReadOnlyList<InspectionTierChoice> tiers,
        IReadOnlyList<InspectionEnchantChoice> enchantments,
        ETier defaultTier,
        EEnchantmentType? defaultEnchantment
    )
    {
        Tiers = tiers;
        Enchantments = enchantments;
        DefaultTier = defaultTier;
        DefaultEnchantment = defaultEnchantment;
    }

    public IReadOnlyList<InspectionTierChoice> Tiers { get; }

    public IReadOnlyList<InspectionEnchantChoice> Enchantments { get; }

    public ETier DefaultTier { get; }

    public EEnchantmentType? DefaultEnchantment { get; }

    public bool HasMultipleTiers => Tiers.Count > 1;

    public bool HasMultipleEnchantments => Enchantments.Count > 1;

    /// <summary>
    /// Builds the available axes for a locked card, or returns <c>null</c> when the card has
    /// neither a second quality nor a second enchantment to inspect.
    /// </summary>
    public static InspectionVariantOptions? Build(CardTooltipData? tooltipData)
    {
        if (
            tooltipData == null
            || !CollectionTierTooltipRegistry.Contains(tooltipData.CardInstance)
        )
            return null;

        var card = tooltipData.CardInstance;
        var tiers = BuildTiers(tooltipData, card);
        var enchantments = BuildEnchantments(card);
        if (tiers.Count < 2 && enchantments.Count < 2)
            return null;

        return new InspectionVariantOptions(
            tiers,
            enchantments,
            card.Tier,
            (card as ItemCard)?.Enchantment.ToManagedNullable()
        );
    }

    private static List<InspectionTierChoice> BuildTiers(CardTooltipData tooltipData, Card card)
    {
        var result = new List<InspectionTierChoice>();
        if (tooltipData.CardTemplate.TryCast<IHasTierData>() is not { } tierData)
            return result;

        var startingTier = tooltipData.CardTemplate.StartingTier;
        var available = tierData
            .Tiers.ToManagedDictionary()
            .Keys.Where(tier => tier >= startingTier)
            .OrderBy(tier => tier);
        foreach (var tier in available)
            result.Add(new InspectionTierChoice(tier, TierLabel(tier), tier == card.Tier));
        return result;
    }

    private static List<InspectionEnchantChoice> BuildEnchantments(Card card)
    {
        var result = new List<InspectionEnchantChoice>();
        if (card is not ItemCard itemCard || itemCard.GetEnchantments() is not { } enchantments)
            return result;

        var current = itemCard.Enchantment.ToManagedNullable();
        var types = enchantments.ToManagedKeys().OrderBy(type => (int)type).ToArray();
        if (types.Length == 0)
            return result;

        // "No enchantment" is a real inspection state and always offered first.
        result.Add(new InspectionEnchantChoice(null, NoEnchantmentLabel(), current == null));
        foreach (var type in types)
        {
            result.Add(
                new InspectionEnchantChoice(
                    type,
                    EnchantmentLabel(type),
                    current == type
                )
            );
        }
        return result;
    }

    // Shared chip copy that no other owner edits; the Quality labels themselves stay in the
    // Collection panel text catalog so inspection and the filter row never disagree.
    private static readonly LocalizedTextSet NoEnchantmentText = new(
        "None",
        "无",
        "無"
    );

    private static string TierLabel(ETier tier) =>
        Game.CollectionPanel.CollectionPanelText.Tier(tier);

    private static string EnchantmentLabel(EEnchantmentType type) =>
        ItemEnchantPreviewFormatting.GetEnchantmentLabel(type);

    private static string NoEnchantmentLabel() => L.Resolve(NoEnchantmentText);
}
