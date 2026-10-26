#if BPP_ANDROID
#nullable enable
using HarmonyLib;
using TheBazaar.Tooltips;
using TheBazaar.UI.Tooltips;
using UnityEngine.EventSystems;

namespace BazaarPlusPlus.Patches.CollectionPanel;

[HarmonyPatch(typeof(CardController), nameof(CardController.OnPointerUp))]
internal static class InRunItemClickInspectionPatch
{
    [HarmonyPrepare]
    private static bool Prepare() =>
        AccessTools.Method(typeof(CardController), nameof(CardController.OnPointerUp)) != null;

    [HarmonyPrefix]
    private static void Prefix(
        CardController __instance,
        PointerEventData eventData,
        out IntPtr __state
    )
    {
        __state = IntPtr.Zero;
        var item = __instance?.TryCast<ItemController>();
        if (
            item == null
            || eventData == null
            || eventData.dragging
            || eventData.button != PointerEventData.InputButton.Left
            || item.IsDragging
            || item._movedSincePress
            || item._holdLockTriggered
            || !item.IsCardVisible
            || item.IsPurchasedFromClick
            || item.CardData == null
        )
            return;
        __state = item.CardData.Pointer;
    }

    [HarmonyPostfix]
    private static void Postfix(CardController __instance, IntPtr __state)
    {
        var item = __instance?.TryCast<ItemController>();
        if (
            __state == IntPtr.Zero
            || item == null
            || item.IsDragging
            || item.IsPurchasedFromClick
            || !item.IsCardVisible
            || item.CardData == null
            || item.CardData.Pointer != __state
        )
            return;

        // Android CardController.MainPointerUp selects on the first release and only calls
        // ProceedClick on a later release. Observe OnPointerUp without replaying either native
        // action; replaying ProceedClick here could purchase/select/fuse the card instead.
        // Capture gesture guards before native code can clear its long-press/drag state.
        item.ShowTooltips();
        var controller = TheBazaar.Data.TooltipParentComponent?.CardTooltipController;
        if (
            controller == null
            || controller.IsLocked
            || !controller.isActiveAndEnabled
            || controller.CurrentTooltipData.TryCast<CardTooltipData>() is not { } data
            || data.CardInstance == null
            || data.CardInstance.Pointer != __state
        )
            return;
        controller.Lock();
    }
}
#endif
