#nullable enable
using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Prerequisites;
using BazaarGameShared.Domain.Prerequisites.Conditionals;
#if BPP_ANDROID
using Il2CppInterop.Runtime.InteropTypes;
#endif

namespace BazaarPlusPlus.GameInterop.Cards;

/// <summary>
/// Resolves the encounter template that accepts a package. Package abilities gate their sell
/// rewards on the current encounter's stable template ID; that runtime rule is authoritative.
/// </summary>
internal static class PackageMerchantIdentity
{
    internal static bool TryResolveMerchantTemplateId(
        ITCard? packageTemplate,
        out Guid merchantTemplateId
    )
    {
        merchantTemplateId = Guid.Empty;
        if (
            packageTemplate == null
            || !PackageIdentity.IsPackage(packageTemplate.HiddenTags?.ToManagedStructSet())
            || packageTemplate.Abilities == null
        )
            return false;

        foreach (var ability in packageTemplate.Abilities.Values)
        {
            if (ability?.Prerequisites == null)
                continue;

            foreach (var prerequisite in ability.Prerequisites)
            {
#if BPP_ANDROID
                // IL2CPP projects interfaces as classes, so the interface-typed value cannot be
                // pattern-matched against the concrete type; the runtime cast must be explicit.
                var run = prerequisite.TryCast<TPrerequisiteRun>();
                var conditional = run?.Conditions.TryCast<TRunConditionalCurrentEncounter>();
                var cardConditional = conditional?.Conditions.TryCast<TCardConditionalId>();
                if (cardConditional is null || cardConditional.IsNot)
                    continue;
                var candidate = cardConditional.Id.ToSystemGuid();
#else
                if (
                    prerequisite
                        is not TPrerequisiteRun
                        {
                            Conditions: TRunConditionalCurrentEncounter
                            {
                                Conditions: TCardConditionalId { IsNot: false, Id: var candidate },
                            },
                        }
                    || candidate == Guid.Empty
                )
                    continue;
#endif

                if (candidate == Guid.Empty)
                    continue;

                if (merchantTemplateId == Guid.Empty)
                {
                    merchantTemplateId = candidate;
                    continue;
                }

                if (merchantTemplateId != candidate)
                {
                    merchantTemplateId = Guid.Empty;
                    return false;
                }
            }
        }

        return merchantTemplateId != Guid.Empty;
    }
}
