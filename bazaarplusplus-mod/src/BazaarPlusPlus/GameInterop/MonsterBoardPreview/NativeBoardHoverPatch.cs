using BazaarPlusPlus.GameInterop.CardPreview;
#if BPP_ANDROID
using BazaarPlusPlus.Patches;
#endif
using HarmonyLib;
using TheBazaar.UI;

namespace BazaarPlusPlus.GameInterop.MonsterBoardPreview;

[HarmonyPatch(typeof(CardPreviewBase), nameof(CardPreviewBase.OnHover))]
internal static class NativeBoardHoverPatch
{
    [HarmonyPrefix]
    private static void Prefix(CardPreviewBase __instance) =>
        NativeCardPreviewHost.ObserveBorrowedHover(__instance, true);
}

[HarmonyPatch(typeof(CardPreviewBase), nameof(CardPreviewBase.OnHoverOut))]
internal static class NativeBoardHoverOutPatch
{
    [HarmonyPrefix]
    private static bool Prefix(CardPreviewBase __instance)
    {
#if BPP_ANDROID
        // A long-pressed board card keeps its hover until the inspection it asked for ends; the
        // finger lifting would otherwise hide or unlock that tooltip.
        if (BppPatchHost.Features.CollectionInspection.HoldsPreviewHover(__instance))
            return false;
#endif
        NativeCardPreviewHost.ObserveBorrowedHover(__instance, false);
        return true;
    }
}

[HarmonyPatch(typeof(CardPreviewBase), "OnDestroy")]
internal static class NativeBoardHoverDestroyedPatch
{
    [HarmonyPrefix]
    private static void Prefix(CardPreviewBase __instance) =>
        NativeCardPreviewHost.ForgetBorrowed(__instance);
}
