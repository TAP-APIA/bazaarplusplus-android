#nullable enable
using System.Text;
using BazaarPlusPlus.Game.CollectionPanel;
using BazaarPlusPlus.Game.CollectionPanel.Tooltips;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using HarmonyLib;
using TheBazaar.Tooltips;
using TheBazaar.UI.Tooltips;

namespace BazaarPlusPlus.Patches.CollectionPanel;

[HarmonyPatch(typeof(CardTooltipData), nameof(CardTooltipData.GetActiveAbilityTooltipBlock))]
internal static class CollectionTierActiveTooltipPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
#if BPP_ANDROID
    private static void Postfix(
        CardTooltipData __instance,
        ref Il2CppSystem.Collections.Generic.List<TooltipSegment> __result
    )
    {
        _ = __instance;
        _ = __result;
        return;
    }
#else
    private static void Postfix(CardTooltipData __instance, ref List<TooltipSegment> __result)
    {
        if (CollectionTierTooltipPreview.IsRenderingVariant)
            return;

        try
        {
            CollectionTierTooltipPreview.MergeActive(__instance, __result);
        }
        catch (Exception ex)
        {
            CollectionTierTooltipLog.ReportDegraded(CollectionTierField.Active, ex);
        }
    }
#endif
}

[HarmonyPatch(typeof(CardTooltipData), nameof(CardTooltipData.GetPassiveTooltipBlock))]
internal static class CollectionTierPassiveTooltipPatch
{
#if BPP_ANDROID
    // The ARM64 detour cannot safely marshal this reference-bearing tuple return.
    // Skip the detour itself: returning early from the postfix still crosses that ABI.
    [HarmonyPrepare]
    private static bool Prepare() => false;
#endif

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(
        CardTooltipData __instance,
#if BPP_ANDROID
        Il2CppSystem.ValueTuple<
            Il2CppSystem.Text.StringBuilder,
            Il2CppSystem.Nullable<TooltipSegment>
        > __result
#else
        ref ValueTuple<StringBuilder, TooltipSegment?> __result
#endif
    )
    {
        if (CollectionTierTooltipPreview.IsRenderingVariant)
            return;

        try
        {
#if BPP_ANDROID
            CollectionTierTooltipPreview.MergePassive(__instance, __result);
#else
            CollectionTierTooltipPreview.MergePassive(__instance, ref __result);
#endif
        }
        catch (Exception ex)
        {
            CollectionTierTooltipLog.ReportDegraded(CollectionTierField.Passive, ex);
        }
    }
}

[HarmonyPatch(typeof(CooldownRenderer), nameof(CooldownRenderer.RenderFromTooltip))]
internal static class CollectionTierCooldownTooltipPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(CooldownRenderer __instance, CardTooltipData tooltipData)
    {
        try
        {
            if (
                !CollectionTierTooltipPreview.TryGetTierAttributeValues(
                    tooltipData,
                    BazaarGameShared.Domain.Core.Types.ECardAttributeType.CooldownMax,
                    "0.#",
                    out var values
                )
            )
                return;

            if (values.Count == 2)
            {
                __instance.SetCooldown(values[0].Text, canFuse: true, values[1].Text);
                return;
            }

            __instance.SetCooldown(CollectionTierTooltipTextMerger.MergeCooldown(values));
        }
        catch (Exception ex)
        {
            CollectionTierTooltipLog.ReportDegraded(CollectionTierField.Cooldown, ex);
        }
    }
}

internal static class CollectionTierTooltipLog
{
    internal static void ReportDegraded(CollectionTierField tierField, Exception exception) =>
        BppLog.WarnEvent(
            new BppLogEvent(
                BppLogFeatureScope.CollectionPanel,
                "collection_panel.tier_tooltip.degraded",
                storm: ["tier_field", "reason_code"]
            ),
            exception,
            ("tier_field", tierField),
            ("reason_code", CollectionPanelLogReasonCode.TierTooltipMergeException)
        );
}
