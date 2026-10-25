#nullable enable
#pragma warning disable CS0436
using BazaarGameShared.Domain.Cards;
using BazaarPlusPlus.GameInterop.Cards;
using BazaarPlusPlus.GameInterop.StaticCards;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using HarmonyLib;
using TheBazaar.Tooltips;
#if !BPP_ANDROID
using System.Text;
#endif

namespace BazaarPlusPlus.Patches.Tooltips;

// Package cards already identify their accepting merchant by stable encounter-template GUID.
// Append the native-compiled merchant/rule text while the game is still building its passive
// tooltip string. The native renderer, enchant-preview section host, and layout system then consume
// the complete block in their normal order.
//
// Android projects a large reference-bearing ValueTuple return. Recognizing its size does not
// establish ARM64 return-buffer compatibility; keep the native method unpatched on that platform.
[HarmonyPatch(typeof(CardTooltipData), nameof(CardTooltipData.GetPassiveTooltipBlock))]
internal static class PackageMerchantSummaryTooltipPatch
{
#if BPP_ANDROID
    // Shares the large tuple return with CollectionTierPassiveTooltipPatch. Do not
    // install either detour until ARM64 return-buffer marshalling is supported.
    [HarmonyPrepare]
    private static bool Prepare() => false;
#endif

    // The first successful append logs one applied event per process.
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Normal)]
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
        try
        {
            var packageTemplate = ResolvePackageTemplate(__instance);
            if (packageTemplate == null)
                return;

            if (
                !PackageMerchantSummary.TryResolve(
                    packageTemplate,
                    out var summary,
                    out var merchantTemplateId,
                    out var failureReason
                )
            )
            {
                ReportDegraded(failureReason, merchantTemplateId);
                return;
            }

            // Item1 is the projected StringBuilder on Android and the managed one on desktop; both
            // expose Length and Append(string), so the append keeps one code path.
            var passiveText = __result.Item1;
            if (passiveText == null || passiveText.Length == 0)
            {
                ReportDegraded(
                    PackageMerchantSummaryFailureReason.PassiveTextUnavailable,
                    merchantTemplateId
                );
                return;
            }

            PackageMerchantSummaryText.AppendToPassiveBlock(passiveText, summary);
            ReportApplied(merchantTemplateId, summary.Length);
        }
        catch (Exception ex)
        {
            ReportDegraded(PackageMerchantSummaryFailureReason.RenderException, Guid.Empty, ex);
        }
    }

    private static ITCard? ResolvePackageTemplate(CardTooltipData tooltipData)
    {
        if (PackageIdentity.IsPackage(tooltipData.CardTemplate?.HiddenTags?.ToManagedStructSet()))
            return tooltipData.CardTemplate;

        var staticData = BppStaticDataAccess.TryGetReadyManagerObject();
        var canonicalTemplate = BppStaticDataAccess.GetCardTemplate(
            staticData,
            tooltipData.CardInstance.TemplateId.ToSystemGuid()
        );
        return PackageIdentity.IsPackage(canonicalTemplate?.HiddenTags?.ToManagedStructSet())
            ? canonicalTemplate?.TryCast<ITCard>()
            : null;
    }

    // One Info line per process, not one per append: the summary lands on every package tooltip, so
    // per-append logging would flood the console during normal play. The single line is what proves
    // on device that the projected tuple reached this postfix and that the append survived the
    // detour's copy-back. Failures never reach here; they go through ReportDegraded.
    private static int _appliedReported;

    private static void ReportApplied(Guid merchantTemplateId, int summaryLength)
    {
        if (Interlocked.Exchange(ref _appliedReported, 1) != 0)
            return;

        BppLog.InfoEvent(
            new BppLogEvent(
                BppLogFeatureScope.Tooltips,
                "tooltips.package_merchant_summary.applied"
            ),
            ("merchant_template_id", merchantTemplateId),
            ("summary_length", summaryLength)
        );
    }

    private static void ReportDegraded(
        PackageMerchantSummaryFailureReason reasonCode,
        Guid merchantTemplateId,
        Exception? exception = null
    )
    {
        var fields = new BppLogField[]
        {
            ("reason_code", reasonCode),
            ("merchant_template_id", merchantTemplateId),
        };
        if (exception == null)
            BppLog.WarnEvent(
                new BppLogEvent(
                    BppLogFeatureScope.Tooltips,
                    "tooltips.package_merchant_summary.degraded",
                    storm: ["reason_code"]
                ),
                fields
            );
        else
            BppLog.WarnEvent(
                new BppLogEvent(
                    BppLogFeatureScope.Tooltips,
                    "tooltips.package_merchant_summary.degraded",
                    storm: ["reason_code"]
                ),
                exception,
                fields
            );
    }
}
