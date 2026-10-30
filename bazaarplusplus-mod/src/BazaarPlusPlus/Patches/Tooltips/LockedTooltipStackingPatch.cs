#nullable enable
#pragma warning disable CS0436
#if BPP_ANDROID
using HarmonyLib;
using TheBazaar.UI.Tooltips;

namespace BazaarPlusPlus.Patches.Tooltips;

// A long-pressed tooltip stays locked while the cards on its monster board open their own
// tooltips from another pooled clone. Every clone's root canvas shares one sorting order, so
// which clone draws on top follows pool order: once the pool has been reused (from the second
// encounter on) the locked clone covers the card tooltip it spawned. Raise the clone being
// shown one step above the shared order whenever another clone is locked.
[HarmonyPatch(typeof(CardTooltipController), nameof(CardTooltipController.ShowTooltipController))]
internal static class LockedTooltipStackingShowPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardTooltipController __instance) =>
        LockedTooltipStacking.OnShown(__instance);
}

[HarmonyPatch(typeof(CardTooltipController), nameof(CardTooltipController.ResetValues))]
internal static class LockedTooltipStackingResetPatch
{
    [HarmonyFinalizer]
    private static void Finalizer(CardTooltipController __instance) =>
        LockedTooltipStacking.Lower(__instance);
}

[HarmonyPatch(typeof(CardTooltipController), nameof(CardTooltipController.ClearCurrentCard))]
internal static class LockedTooltipStackingClearPatch
{
    [HarmonyPrefix]
    private static void Prefix(CardTooltipController __instance) =>
        LockedTooltipStacking.Lower(__instance);
}

[HarmonyPatch(typeof(CardTooltipController), "OnDisable")]
internal static class LockedTooltipStackingDisablePatch
{
    [HarmonyPrefix]
    private static void Prefix(CardTooltipController __instance) =>
        LockedTooltipStacking.Lower(__instance);
}

[HarmonyPatch(typeof(CardTooltipController), nameof(CardTooltipController.OnDestroy))]
internal static class LockedTooltipStackingDestroyPatch
{
    [HarmonyPrefix]
    private static void Prefix(CardTooltipController __instance)
    {
        LockedTooltipStacking.Lower(__instance);
        LockedTooltipStacking.Forget(__instance);
    }
}

internal static class LockedTooltipStacking
{
    // The tooltip pool holds a handful of clones; every one that was ever shown is tracked.
    private static readonly List<CardTooltipController> Seen = new();

    internal static void OnShown(CardTooltipController controller)
    {
        if (controller == null)
            return;
        // IL2CPP may hand out a fresh wrapper per call, so identity is the native instance id.
        var id = controller.GetInstanceID();
        Seen.RemoveAll(seen => seen == null || seen.GetInstanceID() == id);
        Seen.Add(controller);

        var anotherLocked = false;
        foreach (var other in Seen)
        {
            if (other.GetInstanceID() == id || !other.isActiveAndEnabled || !other.IsLocked)
                continue;
            anotherLocked = true;
            // A clone raised for an earlier lock that is now locked itself must drop back,
            // or it ties with the clone raised above it.
            Lower(other);
        }

        if (anotherLocked && !controller.IsLocked)
            TooltipLayerOverride.SetElevated(
                controller,
                elevated: true,
                TooltipElevationReason.AboveLockedTooltip
            );
        else
            Lower(controller);
    }

    internal static void Lower(CardTooltipController controller) =>
        TooltipLayerOverride.SetElevated(
            controller,
            elevated: false,
            TooltipElevationReason.AboveLockedTooltip
        );

    internal static void Forget(CardTooltipController controller)
    {
        var id = controller.GetInstanceID();
        Seen.RemoveAll(seen => seen == null || seen.GetInstanceID() == id);
    }
}
#endif
