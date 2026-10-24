#nullable enable
#pragma warning disable CS0436
#if !BPP_ANDROID
using System.Text;
#endif
using BazaarGameShared.Domain.Cards;
using BazaarPlusPlus.GameInterop.Cards;
using BazaarPlusPlus.GameInterop.StaticCards;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using HarmonyLib;
using TheBazaar.Tooltips;

namespace BazaarPlusPlus.Patches.Tooltips;

// Package cards already identify their accepting merchant by stable encounter-template GUID.
// Append the native-compiled merchant/rule text while the game is still building its passive
// tooltip string. The native renderer, enchant-preview section host, and layout system then consume
// the complete block in their normal order.
//
// Android IL2CPP projects this method as Il2CppSystem.ValueTuple<Il2CppSystem.Text.StringBuilder,
// Il2CppSystem.Nullable<TheBazaar.Tooltips.TooltipSegment>>, so the postfix names that tuple instead
// of the managed one - a mismatch here is an ABI mismatch, not a missing capability. The tuple is
// far wider than 16 bytes (its Item2 embeds the whole TooltipSegment), so libil2cpp returns it
// through the hidden return buffer, which Il2CppDetourMethodPatcher already implements on
// non-Windows 64-bit processes (Il2CppInterop.HarmonySupport 1.5.3,
// Il2CppDetourMethodPatcher.IsReturnBufferNeeded: size > 16). Appending into __result.Item1 edits
// the StringBuilder the caller receives; the detour copies the boxed tuple's bytes back afterwards,
// which is why __result is taken by value here and never replaced.
//
// No manual pointer work is done here on purpose: Il2CppClassPointerStore.GetNativeClassPointer and
// il2cpp_object_unbox are only needed to re-derive the tuple's value size or to unbox it by hand,
// and their boxed-vs-unboxed convention for this tuple is 待真机验证. Reading the projected Item1
// property keeps the postfix on the same accessor path as the rest of the port.
[HarmonyPatch(typeof(CardTooltipData), nameof(CardTooltipData.GetPassiveTooltipBlock))]
internal static class PackageMerchantSummaryTooltipPatch
{
    // 待真机验证: neither the arm64 detour ABI nor the in-place append can be observed off device.
    // The first successful append logs one `applied` event per process; a package tooltip that still
    // shows no summary while `applied` fired means the caller read its pre-detour buffer, and a
    // summary that never appears while `degraded` reports a reason_code means resolution failed.
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
