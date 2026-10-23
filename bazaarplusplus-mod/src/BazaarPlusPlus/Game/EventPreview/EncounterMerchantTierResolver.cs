#nullable enable
using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Cards.Encounter.Event;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Spawning.SpawnBehaviors;
using BazaarGameShared.Domain.Spawning.SpawningContexts;

namespace BazaarPlusPlus.Game.EventPreview;

internal readonly struct EncounterMerchantTierPolicy
{
    public EncounterMerchantTierPolicy(ETier? fixedTier, bool usesDayDistribution)
    {
        FixedTier = fixedTier;
        UsesDayDistribution = usesDayDistribution;
    }

    public ETier? FixedTier { get; }

    public bool UsesDayDistribution { get; }
}

internal static class EncounterMerchantTierResolver
{
    public static EncounterMerchantTierPolicy Resolve(TCardBase? template)
    {
        if (
            template is not TCardEncounterEvent eventTemplate
            || eventTemplate.SelectionContext?.SpawnContext.TryCast<TSpawnContextQuery>() is not { } spawnContext
        )
            return new EncounterMerchantTierPolicy(fixedTier: null, usesDayDistribution: true);

        ETier? fixedTier = null;
        var ignoresDayTable = false;
        if (spawnContext.Behaviors == null)
            return new EncounterMerchantTierPolicy(fixedTier: null, usesDayDistribution: true);

        foreach (var behavior in spawnContext.Behaviors)
        {
            var ignoreTierTable = behavior.TryCast<TSpawnBehaviorIgnoreTierTable>();
            if (ignoreTierTable != null && ignoreTierTable.IgnoreTierTable)
                ignoresDayTable = true;

            var tierBehavior = behavior.TryCast<TSpawnBehaviorTier>();
            if (tierBehavior != null && !tierBehavior.IsNot && tierBehavior.Tiers.Count == 1)
            {
                foreach (var tier in tierBehavior.Tiers.ToManagedStructSet())
                    fixedTier = tier;
            }
        }

        return fixedTier.HasValue
            ? new EncounterMerchantTierPolicy(fixedTier, usesDayDistribution: false)
            : new EncounterMerchantTierPolicy(
                fixedTier: null,
                usesDayDistribution: !ignoresDayTable
            );
    }
}
