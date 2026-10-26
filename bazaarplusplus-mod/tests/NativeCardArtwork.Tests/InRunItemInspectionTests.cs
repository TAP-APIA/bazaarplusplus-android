#if BPP_ANDROID
using System.Reflection;
using BazaarPlusPlus.Patches.CollectionPanel;
using TheBazaar.Tooltips;
using TheBazaar.UI.Tooltips;
using UnityEngine.EventSystems;

internal static class InRunItemInspectionTests
{
    internal static void Run()
    {
        var controller = ResetTooltip();
        var card = new ItemController { PurchaseOnProceed = true };
        Release(card, new());
        Check(card.SelectCalls == 1 && card.ProceedCalls == 0,
            "First native release must only select, not proceed/purchase");
        Check(card.ShowCalls == 1 && controller.LockCalls == 1,
            "First release did not open inspection through the actual pointer-up boundary");
        Check(controller.IsLocked && controller.CurrentTooltipData?.CardInstance == card.CardData,
            "Inspection was not locked to the clicked card");

        // Native purchase clicks must still execute exactly once, never be replayed by the patch.
        controller = ResetTooltip();
        Release(card, new());
        Check(card.ProceedCalls == 1 && card.PurchaseCalls == 1,
            "Native second-release purchase was suppressed or replayed");
        Check(card.ShowCalls == 1 && controller.LockCalls == 0,
            "Purchase click opened inspection");

        Reject(new ItemController(), new() { dragging = true }, "pointer drag");
        Reject(new ItemController(), new() { button = PointerEventData.InputButton.Right }, "secondary");
        Reject(new ItemController(), new() { button = PointerEventData.InputButton.Middle }, "middle");
        Reject(new ItemController { IsDragging = true }, new(), "item drag");
        Reject(new ItemController { _movedSincePress = true }, new(), "moved gesture");
        Reject(new ItemController { _holdLockTriggered = true }, new(), "native long press");
        Reject(new ItemController { IsPurchasedFromClick = true }, new(), "already purchased");
        Reject(new ItemController { IsCardVisible = false }, new(), "hidden item");
        Reject(new ItemController { CardData = null }, new(), "missing card data");

        controller = ResetTooltip();
        var nonItem = new CardController();
        Release(nonItem, new());
        Check(nonItem.NativeUpCalls == 1 && controller.LockCalls == 0,
            "Non-item input was changed");

        controller = ResetTooltip();
        card = new ItemController { PublishTooltip = false };
        controller.isActiveAndEnabled = true;
        controller.CurrentTooltipData = new() { CardInstance = new() { Pointer = new IntPtr(99) } };
        Release(card, new());
        Check(controller.LockCalls == 0, "Stale hover target was locked");

        controller = ResetTooltip();
        card = new ItemController { PublishTooltip = false };
        controller.CurrentTooltipData = new() { CardInstance = card.CardData };
        Release(card, new());
        Check(controller.LockCalls == 0, "Inactive matching tooltip was locked");

        controller = ResetTooltip();
        card = new ItemController { RebindOnRelease = true };
        Release(card, new());
        Check(card.ShowCalls == 0 && controller.LockCalls == 0,
            "Card rebound by native input was inspected using the old release");

        controller = ResetTooltip();
        controller.IsLocked = true;
        Release(new ItemController(), new());
        Check(controller.LockCalls == 0, "Already locked tooltip was locked a second time");
        Console.WriteLine("Native first-select pointer-up inspection and gesture/purchase/stale-target tests pass.");
    }

    private static CardTooltipController ResetTooltip()
    {
        var controller = new CardTooltipController();
        TheBazaar.Data.TooltipParentComponent = new() { CardTooltipController = controller };
        return controller;
    }

    private static void Reject(ItemController card, PointerEventData pointer, string reason)
    {
        var controller = ResetTooltip();
        Release(card, pointer);
        Check(card.NativeUpCalls == 1, $"{reason}: native input was suppressed");
        Check(card.ShowCalls == 0 && controller.LockCalls == 0, $"{reason}: opened inspection");
    }

    private static void Release(CardController card, PointerEventData pointer)
    {
        var patch = typeof(InRunItemClickInspectionPatch);
        var target = patch.GetCustomAttribute<HarmonyLib.HarmonyPatch>()!;
        Check(target.Type == typeof(CardController) && target.Method == nameof(CardController.OnPointerUp),
            "Patch must target the real pointer-up entry, not second-click-only ProceedClick");
        var prefix = patch.GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)!;
        Check(prefix.ReturnType == typeof(void), "Inspection patch must not suppress native input");
        object?[] arguments = [card, pointer, IntPtr.Zero];
        prefix.Invoke(null, arguments);
        card.OnPointerUp(pointer);
        patch.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [card, arguments[2]]);
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}

namespace UnityEngine.EventSystems
{
    internal sealed class PointerEventData
    {
        internal enum InputButton { Left, Right, Middle }
        internal InputButton button;
        internal bool dragging;
    }
}

namespace TheBazaar.Tooltips
{
    internal sealed class InspectionTestCard { internal IntPtr Pointer = new(1); }
    internal sealed class CardTooltipData
    {
        internal InspectionTestCard? CardInstance = new();
        internal T? TryCast<T>() where T : class => this as T;
    }
}

namespace TheBazaar.UI.Tooltips
{
    internal class CardController
    {
        internal bool _movedSincePress, _holdLockTriggered;
        internal bool IsPurchasedFromClick;
        internal bool IsCardVisible = true;
        internal InspectionTestCard? CardData = new();
        internal int NativeUpCalls, SelectCalls, ProceedCalls;
        internal bool Selected;
        internal T? TryCast<T>() where T : class => this as T;

        // Native ARM64: MainPointerUp @0x722D4DC branches on _cardState (offset 0x13C).
        // Zero -> virtual Select slot24; nonzero -> ProceedClick slot33, reset, Deselect.
        // OnPointerClick @0x722D5E4 is ret and cannot represent the first-touch entry.
        internal void OnPointerUp(PointerEventData pointer)
        {
            NativeUpCalls++;
            if (!_holdLockTriggered && !_movedSincePress && !pointer.dragging)
            {
                if (!Selected) { SelectCalls++; Selected = true; }
                else { ProceedCalls++; ProceedClick(); Selected = false; }
            }
            // A postfix-only gesture check would miss native state cleared on release.
            _holdLockTriggered = false;
            _movedSincePress = false;
            if (this is ItemController item)
            {
                item.IsDragging = false;
                if (item.RebindOnRelease) CardData = new() { Pointer = new IntPtr(2) };
            }
        }

        protected virtual void ProceedClick() { }
    }

    internal sealed class ItemController : CardController
    {
        internal bool IsDragging;
        internal int ShowCalls, PurchaseCalls;
        internal bool PublishTooltip = true;
        internal bool PurchaseOnProceed, RebindOnRelease;
        protected override void ProceedClick()
        {
            if (!PurchaseOnProceed) return;
            PurchaseCalls++;
            IsPurchasedFromClick = true;
        }
        internal void ShowTooltips()
        {
            ShowCalls++;
            if (!PublishTooltip) return;
            var controller = TheBazaar.Data.TooltipParentComponent!.CardTooltipController;
            controller.CurrentTooltipData = new() { CardInstance = CardData };
            controller.isActiveAndEnabled = true;
        }
    }
    internal sealed class CardTooltipController
    {
        internal bool IsLocked, isActiveAndEnabled;
        internal int LockCalls;
        internal CardTooltipData CurrentTooltipData = new();
        internal void Lock() { LockCalls++; IsLocked = true; }
    }
}

namespace TheBazaar
{
    internal static class Data { internal static TestTooltipParent? TooltipParentComponent; }
    internal sealed class TestTooltipParent
    {
        internal CardTooltipController CardTooltipController = new();
    }
}

namespace HarmonyLib
{
    internal sealed class HarmonyPostfix : Attribute;
    internal sealed class HarmonyPrepare : Attribute;
    internal static class AccessTools
    {
        internal static MethodInfo? Method(Type type, string name) => type.GetMethod(
            name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    }
}
#endif
