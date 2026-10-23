#nullable enable
using TheBazaar.UI.Tooltips;
using UnityEngine;

namespace BazaarPlusPlus.GameInterop.Tooltips;

/// <summary>
/// Tap-driven nested tooltips for the inspection overlay.
///
/// On desktop a nested tooltip appears while the pointer sits on one of the
/// <see cref="InteractableTooltipElement"/> rows the renderer builds for effect text, price tags,
/// cooldown and ammo. <see cref="NestedTooltipService.OnPointerMove"/> drives that with a hover
/// raycast, so a locked tooltip on a touchscreen has no trigger at all: a tap never hovers.
///
/// This adapter replaces only the trigger. It resolves the row under the tap and calls the game's
/// own <see cref="INestedTooltipService.ShowNestedTooltip"/>, so the popup keeps the native header,
/// attribute breakdown, tier colouring, offset and fade the desktop client shows. Nothing about
/// the copy is reimplemented here.
///
/// Rows that the native renderer marks as non-interactive (<see cref="InteractableTooltipElement.ShowNestedTooltip"/>
/// is false, e.g. plain flavour text) are skipped, so a tap on ordinary prose does nothing.
/// </summary>
internal sealed class InspectionNestedTooltips
{
    private readonly CardTooltipController _controller;
    private readonly INestedTooltipService? _service;
    private readonly NestedTooltipController? _nested;
    private readonly BppFieldAccessor? _parentObject;
    private readonly BppFieldAccessor? _canvasGroup;
    private readonly List<InteractableTooltipElement> _elements = new();
    private InteractableTooltipElement? _shown;

    public InspectionNestedTooltips(CardTooltipController controller)
    {
        _controller = controller;
        _service = BppFieldAccessor
            .Find(typeof(CardTooltipController), "_nestedTooltipService")
            ?.GetValue<INestedTooltipService>(controller);
        _nested = BppFieldAccessor
            .Find(typeof(CardTooltipController), "nestedTooltipController")
            ?.GetValue<NestedTooltipController>(controller);
        var nestedType = typeof(NestedTooltipController);
        _parentObject = BppFieldAccessor.Find(nestedType, "parentObject");
        _canvasGroup = BppFieldAccessor.Find(nestedType, "canvasGroup");
    }

    public bool IsAvailable => _service != null && _nested != null;

    /// <summary>
    /// True while the native nested tooltip popup is on screen. The popup fades, so the canvas
    /// group alpha describes what the player sees rather than what was last requested, and it
    /// stays accurate when the native pointer path opened the popup instead of this adapter.
    /// </summary>
    public bool IsOpen => PopupVisible();

    /// <summary>
    /// Applies the release of a tap at <paramref name="point"/> and reports whether the tap was
    /// consumed.
    ///
    /// Showing is idempotent: desktop keeps the popup open while the pointer rests on a row, so a
    /// repeated tap on that row must not close and reopen it. Only a tap on the popup itself closes
    /// the popup; a tap that misses every row is left to the caller's own dismissal rule.
    /// </summary>
    public bool ApplyTap(Vector2 point)
    {
        if (!IsAvailable)
            return false;
        var open = PopupVisible();
        if (open && IsPopupPoint(point))
        {
            Hide();
            return true;
        }
        RefreshElements();
        var target = HitTest(point);
        if (target == null)
        {
            Hide();
            // With a popup on screen the tap meant "dismiss the popup", so this one is consumed;
            // dismissing the whole inspection would be a surprising extra step.
            return open;
        }
        if (open && ReferenceEquals(target, _shown))
            return true;
        return Show(target) || open;
    }

    public void Hide()
    {
        var wasOpen = _shown != null || PopupVisible();
        _shown = null;
        if (_service == null || !wasOpen)
            return;
        try
        {
            _service.HideNestedTooltip();
        }
        catch (Exception)
        {
            // A tooltip torn down by the native lock flow is already hidden.
        }
    }

    private bool Show(InteractableTooltipElement target)
    {
        if (_service == null)
            return false;
        try
        {
            _service.ShowNestedTooltip(target);
            _shown = target;
            return true;
        }
        catch (Exception)
        {
            _shown = null;
            return false;
        }
    }

    /// <summary>True when the point is inside the open nested tooltip popup.</summary>
    private bool IsPopupPoint(Vector2 point)
    {
        if (_nested == null)
            return false;
        if (_parentObject?.GetValue(_nested) is not RectTransform rect || rect == null)
            return false;
        return ScreenRect(rect).Contains(point);
    }

    private bool PopupVisible()
    {
        if (_nested == null)
            return false;
        // The popup fades through this canvas group, so its alpha is what the player sees.
        if (_canvasGroup?.GetValue(_nested) is CanvasGroup group)
            return group.alpha > .01f;
        // Without an observable fade, only trust what this adapter last requested. Assuming the
        // popup is always open would make every tap look like a dismissal.
        return _shown != null;
    }

    private InteractableTooltipElement? HitTest(Vector2 point)
    {
        // The renderer flags exactly those rows that own a nested breakdown, and the native
        // service uses the same flag. Prefer flagged rows so flavour-only prose stays inert.
        var flagged = HitTest(point, requireFlag: true);
        if (flagged != null)
            return flagged;
        // If no row in this tooltip carries the flag at all, the content was built without it and
        // requiring it would make the popup unreachable. Fall back rather than silently disabling
        // the feature; the native service still decides what, if anything, it can show.
        return AnyFlagged ? null : HitTest(point, requireFlag: false);
    }

    private bool AnyFlagged
    {
        get
        {
            foreach (var element in _elements)
                if (element != null && element.ShowNestedTooltip)
                    return true;
            return false;
        }
    }

    private InteractableTooltipElement? HitTest(Vector2 point, bool requireFlag)
    {
        InteractableTooltipElement? best = null;
        var bestArea = float.MaxValue;
        foreach (var element in _elements)
        {
            if (element == null || !element.isActiveAndEnabled)
                continue;
            if (requireFlag && !element.ShowNestedTooltip)
                continue;
            if (element.transform.AsRectTransform() is not { } rect)
                continue;
            var bounds = ScreenRect(rect);
            if (bounds.width <= 0f || bounds.height <= 0f || !bounds.Contains(point))
                continue;
            // Rows can overlap while the tooltip relayouts; the smallest one is the most specific.
            var area = bounds.width * bounds.height;
            if (area >= bestArea)
                continue;
            bestArea = area;
            best = element;
        }
        return best;
    }

    // Rebuilt per tap: the renderer recycles rows whenever the description or the selected
    // quality/enchantment changes, so a cached list would address stale components.
    private void RefreshElements()
    {
        _elements.Clear();
        var content = _controller.CanvasContentRectTransform;
        if (content == null)
            return;
        _elements.AddRange(content.GetComponentsInChildren<InteractableTooltipElement>(false));
    }

    private static Rect ScreenRect(RectTransform rect)
    {
        var corners = new Vector3[4];
        rect.GetWorldCornersManaged(corners);
        return Rect.MinMaxRect(
            corners.Min(point => point.x),
            corners.Min(point => point.y),
            corners.Max(point => point.x),
            corners.Max(point => point.y)
        );
    }
}
