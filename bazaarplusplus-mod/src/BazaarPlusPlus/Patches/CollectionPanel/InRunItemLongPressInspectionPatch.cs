#if BPP_ANDROID
#nullable enable
using HarmonyLib;
using TheBazaar.Tooltips;
using TheBazaar.UI.Tooltips;
using UnityEngine.EventSystems;

namespace BazaarPlusPlus.Patches.CollectionPanel;

// Board previews and in-run items share one gesture on touch: a long press inspects, a tap keeps
// the game's own select/purchase behavior. Only the Collection grid inspects on a tap.
[HarmonyPatch(typeof(CardController), nameof(CardController.OnPointerUp))]
internal static class InRunItemLongPressInspectionPatch
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
            || !item._holdLockTriggered
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

        // A native long press skips both MainPointerUp actions (Select on the first release,
        // ProceedClick on a later one), so nothing here can purchase/select/fuse the card.
        // Gesture guards are captured in the prefix because native release clears them.
        // If the native hold already locked the tooltip, OnLocked has opened inspection.
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
