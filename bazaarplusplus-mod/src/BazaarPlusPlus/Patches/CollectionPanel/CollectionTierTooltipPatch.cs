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
    private static void Postfix(CardTooltipData __instance, ref Il2CppSystem.Collections.Generic.List<TooltipSegment> __result)
    {
#if BPP_ANDROID
        _ = __instance;
        _ = __result;
        return;
#else
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
#endif
    }
}

[HarmonyPatch(typeof(CardTooltipData), nameof(CardTooltipData.GetPassiveTooltipBlock))]
internal static class CollectionTierPassiveTooltipPatch
{
#if BPP_ANDROID
    // The native tuple contains a reference-bearing value type. Preserve native text.
    [HarmonyPrepare]
    private static bool Prepare() => false;
#endif
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(
        CardTooltipData __instance,
        ref ValueTuple<StringBuilder, TooltipSegment?> __result
    )
    {
        if (CollectionTierTooltipPreview.IsRenderingVariant)
            return;

        try
        {
            CollectionTierTooltipPreview.MergePassive(__instance, ref __result);
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
