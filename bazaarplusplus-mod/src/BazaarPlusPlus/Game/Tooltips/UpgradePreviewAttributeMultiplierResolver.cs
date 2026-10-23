#nullable enable
using BazaarGameShared.Domain.Cards.Quests;
using BazaarGameShared.Domain.Cards;
using BazaarGameClient.Domain.Models.Cards;
using BazaarGameShared.Domain.Cards.Interfaces;
using BazaarGameShared.Domain.Cards.Item;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Effect;
using BazaarGameShared.Domain.Effect.AuraActions;
using BazaarGameShared.Domain.Prerequisites;
using BazaarGameShared.Domain.Targeting;
using BazaarGameShared.Domain.Values;

namespace BazaarPlusPlus.Game.Tooltips;

internal static class UpgradePreviewAttributeMultiplierResolver
{
    internal readonly record struct Multipliers(float Current, float Projected);

    internal static IReadOnlyDictionary<ECardAttributeType, Multipliers> Resolve(
        ItemCard source,
        ItemCard projected,
        ValueContext sourceValueContext
    )
    {
        var multipliers = new Dictionary<ECardAttributeType, Multipliers>();
        foreach (var auraSource in EnumerateAuraSources(source, sourceValueContext))
        {
            Accumulate(source, auraSource, sourceValueContext, projectedValue: false, multipliers);

            var projectedAuraSource = ReferenceEquals(auraSource, source) ? projected : auraSource;
            Accumulate(
                projected,
                projectedAuraSource,
                sourceValueContext,
                projectedValue: true,
                multipliers
            );
        }

        return multipliers;
    }

    private static void Accumulate(
        ItemCard target,
        Card auraSource,
        ValueContext sourceValueContext,
        bool projectedValue,
        Dictionary<ECardAttributeType, Multipliers> multipliers
    )
    {
        foreach (var aura in EnumerateActiveAuras(auraSource))
        {
            // IL2CPP projects the aura action hierarchy as unrelated classes, so the nested
            // property pattern cannot match; cast first, then check the shape.
            var action = aura.Action.TryCast<TAuraActionCardModifyAttribute>();
            if (
                action == null
                || action.Operation != EAttributeModifierOperation.Multiply
                || !IsActiveOutOfCombat(auraSource, aura, sourceValueContext)
                || !Targets(target, auraSource, action, sourceValueContext)
            )
                continue;

            var auraAttribute = action.AttributeType;
            var value = ResolveValue(action, auraSource, sourceValueContext);
            var current = multipliers.GetValueOrDefault(auraAttribute, new Multipliers(1f, 1f));
            multipliers[auraAttribute] = projectedValue
                ? current with
                {
                    Projected = current.Projected * value,
                }
                : current with
                {
                    Current = current.Current * value,
                };
        }
    }

    private static IEnumerable<Card> EnumerateAuraSources(
        ItemCard source,
        ValueContext sourceValueContext
    )
    {
        yield return source;

        var run = sourceValueContext.Run;
        if (run == null)
            yield break;

        var seen = new HashSet<InstanceId> { source.InstanceId };
        foreach (var player in new[] { run.GetPlayer(), run.GetOpponent() })
        {
            if (player == null)
                continue;

            foreach (
                var card in player
                    .Hand.GetItems()?.ToManaged()
                    .Concat(player.Stash.GetItems()?.ToManaged())
                    .Concat(player.Socket.GetItems()?.ToManaged())
                    .Concat(player.GetSkills()?.ToManaged())
                    .OfType<Card>()
            )
            {
                if (seen.Add(card.InstanceId))
                    yield return card;
            }
        }
    }

    private static IEnumerable<TCardAura> EnumerateActiveAuras(Card card)
    {
        if (card.Template.TryCast<IHasTierData>() is { } tierData)
        {
            foreach (var aura in tierData.GetAuraTemplatesByTier(card.Tier))
                yield return aura;
        }
        else if (card.Template != null)
        {
            foreach (var aura in card.Template.Auras.Values)
                yield return aura;
        }

        if (
            card is ItemCard { Enchantment: not null } item
            && item.Template.TryCast<TCardItem>() is { } itemTemplate
            && itemTemplate.TryGetEnchantmentTemplate(item.Enchantment.Value, out var enchantment)
            && enchantment != null
        )
        {
            foreach (var aura in enchantment.Auras.Values)
                yield return aura;
        }

        if (card.TryCast<ItemCard>() is not { } questItem || questItem.Template.TryCast<TCardItem>() is not { } questTemplate)
            yield break;

        foreach (
            var reward in (questTemplate.Quests?.ToManaged() ?? new List<TQuestGroup>())
                .SelectMany(group => group.Entries.ToManaged())
                .Where(entry => entry.IsComplete(questItem.TryCast<ICard>()!))
                .Select(entry => entry.Reward)
                .Where(reward => reward?.HasAuras == true)
        )
        {
            foreach (var aura in reward!.Auras.Values)
                yield return aura;
        }
    }

    private static bool IsActiveOutOfCombat(
        Card auraSource,
        TCardAura aura,
        ValueContext sourceValueContext
    )
    {
        if ((aura.WorksIn & EEffectWorksIn.OutOfCombatOnly) == 0)
            return false;

        if (
            auraSource.Type == ECardType.Item
            && (
                aura.ActiveIn switch
                {
                    EEffectActiveIn.HandOnly => auraSource.Section.ToManagedNullable() != EInventorySection.Hand,
                    EEffectActiveIn.StashOnly => auraSource.Section.ToManagedNullable() != EInventorySection.Stash,
                    EEffectActiveIn.HandAndStash => auraSource.Section == null,
                    _ => false,
                }
            )
        )
            return false;

        return aura.Prerequisites == null
            || aura.Prerequisites.ToManaged().All(prerequisite =>
                prerequisite.IsSatisfiedBy(
                    new PrereqContext(
                        sourceValueContext.Run,
                        auraSource.TryCast<ICard>()!,
                        sourceValueContext.EventContext
                    )
                )
            );
    }

    private static bool Targets(
        ItemCard target,
        Card auraSource,
        TAuraActionCardModifyAttribute action,
        ValueContext sourceValueContext
    )
    {
        if (sourceValueContext.Run == null)
            return ReferenceEquals(auraSource, target) && action.Target is TTargetCardSelf;

        var context = new TargetingContext(
            sourceValueContext.Run,
            auraSource.TryCast<ICard>()!,
            sourceValueContext.EventContext
        );
        return action
            .Target.GetTargets(context)
            .ToManaged().Any(card => card.GetInstanceId() == target.InstanceId);
    }

    private static float ResolveValue(
        TAuraActionCardModifyAttribute action,
        Card auraSource,
        ValueContext sourceValueContext
    ) =>
        action.Value.GetValue(
            new ValueContext(sourceValueContext.Run, auraSource.TryCast<ICard>()!, sourceValueContext.EventContext)
        );
}
