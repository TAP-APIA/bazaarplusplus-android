#nullable enable
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using BazaarGameShared.Domain.Core.Types;
using BazaarPlusPlus.Infrastructure.UiTokens;
using DG.Tweening;
using TheBazaar.UI.Tooltips;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BazaarPlusPlus.GameInterop.Tooltips;

// A reversible lease on the native tooltip's canvases. ScreenSpaceOverlay also works when
// the native inspection backdrop originally belongs to a camera rendered below BPP's UI.
//
// Android keeps the tooltip canvases parented instead of promoting them to a bare overlay: the
// game already ships a right-half drawer for mobile inspection, and detaching it threw away the
// CanvasScaler geometry and the tray's own layout. BPP only raises the sort order and leaves the
// description, the viewport mask and the tray backdrops exactly as the game laid them out.
internal sealed class NativeInspectionPresentation : IDisposable
{
    private readonly CardTooltipController _controller;
    private readonly List<CanvasLease> _canvases = new();
    private readonly GameObject _root;
    private readonly RectState _tooltipRect;
    private readonly Graphic? _nativeBackground;
    private readonly bool _backgroundEnabled;
    private readonly bool _backgroundRaycast;
    private readonly InspectionBackgroundPress _backgroundPress = new();
#if BPP_ANDROID
    private readonly int _createdFrame = Time.frameCount;
#endif
    private readonly List<Selectable> _controls = new();
    private readonly List<TMP_Dropdown> _dropdowns = new();
    private readonly List<TMP_Text> _captions = new();
    // BPP-owned quality/enchantment chips live outside the native panel subtree, so they
    // register their own hit regions instead of being discovered by PromoteVariantControls.
    private readonly List<RectTransform> _protectedControls = new();
    private InspectionNestedTooltips? _nestedTooltips;
    // Finger travel beyond this many pixels is a drag/scroll, not a tap.
    private const float TapSlop = 18f;
    private Vector2 _tapOrigin;
    private int _tapFrame = -1;
#if !BPP_ANDROID
    private Vector3 _descriptionScale;
#endif
    private static readonly BppFieldAccessor? DropdownList = BppFieldAccessor.Find(
        typeof(TMP_Dropdown),
        "m_Dropdown"
    );
    private bool _disposed;
    private bool _released;
#if BPP_ANDROID
    // The game's own mobile drawer ("Scroll View") keeps its native layout: BPP only snaps it to
    // the show position the native fade-in uses and remembers what it moved so Release can undo it.
    private RectTransform? _nativeTrayRect;
    private Vector2 _nativeTrayAnchoredPosition;
    private bool _nativeTrayCaptured;
    private float _positioningAlpha;
    private bool _positioningAlphaCaptured;
    // The native drawer brings no opaque panel of its own: its backdrop (lockModeBackgroundImage)
    // is disabled so it cannot dim the enlarged card, which otherwise leaves the collection's
    // filter column legible through the description. This plate fills exactly the drawer's rect.
    private RectTransform? _drawerBackdrop;
#endif

    public RectTransform CardRoot { get; }

    public NativeInspectionPresentation(CardTooltipController controller)
    {
        _controller = controller;
        _tooltipRect = new RectState(controller.TooltipRectTransform);
        _root = BppGameObjectFactory.Create(
            "BppCollectionInspection",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(GraphicRaycaster)
        );
        Object.DontDestroyOnLoad(_root);
        var canvas = _root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = BppOverlaySorting.InspectionBackdrop;
        var backdrop = BppGameObjectFactory.Create("DismissBackground", typeof(RectTransform), typeof(Image));
        backdrop.transform.SetParent(_root.transform, false);
        Stretch(backdrop.GetComponent<RectTransform>());
        backdrop.GetComponent<Image>().color = new Color(0f, 0f, 0f, .53f);
#if BPP_ANDROID
        var drawerBackdrop = BppGameObjectFactory.Create(
            "DrawerBackdrop",
            typeof(RectTransform),
            typeof(Image)
        );
        drawerBackdrop.transform.SetParent(_root.transform, false);
        _drawerBackdrop = drawerBackdrop.GetComponent<RectTransform>();
        _drawerBackdrop.anchorMin = _drawerBackdrop.anchorMax = Vector2.zero;
        _drawerBackdrop.pivot = Vector2.zero;
        _drawerBackdrop.anchoredPosition = Vector2.zero;
        _drawerBackdrop.sizeDelta = Vector2.zero;
        var drawerPlate = drawerBackdrop.GetComponent<Image>();
        drawerPlate.color = Colors.CollectionPanelBackground;
        // The plate must never eat taps the drawer and the dismiss background rely on.
        drawerPlate.raycastTarget = false;
#endif
        var card = BppGameObjectFactory.Create("InspectionCard", typeof(RectTransform));
        card.transform.SetParent(_root.transform, false);
        CardRoot = card.GetComponent<RectTransform>();
        CardRoot.anchorMin = CardRoot.anchorMax = CardRoot.pivot = new Vector2(0f, 1f);
        CardRoot.anchoredPosition = Vector2.zero;
        CardRoot.sizeDelta = new Vector2(Screen.width, Screen.height);

        try
        {
            controller.LockingSequence?.Kill();
            controller.LockingSequence = null;
            controller.OnLockSequenceCompletedPublic();
            controller.LockModeCanvasGroup.alpha = 1f;
            _nativeBackground = BppFieldAccessor
                .Find(typeof(CardTooltipController), "lockModeBackgroundImage")
                ?.GetValue<Graphic>(controller);
            if (_nativeBackground != null)
            {
                _backgroundEnabled = _nativeBackground.enabled;
                _backgroundRaycast = _nativeBackground.raycastTarget;
                _nativeBackground.enabled = false;
                _nativeBackground.raycastTarget = false;
            }
            // Capture both hierarchies BEFORE detaching either canvas.
            foreach (
                var native in new[]
                {
                    controller.LockModeCanvas,
                    controller.RootCanvasComponent,
                }.Distinct()
            )
                _canvases.Add(new CanvasLease(native));
#if BPP_ANDROID
            // The mobile drawer only reads as the game's own panel while it keeps the tooltip
            // canvas' CanvasScaler: detaching it resized the 1100px tray to 1414.5px. Promotion is
            // therefore limited to sort order and raycasting on the parented canvases.
            const bool detachCanvas = false;
#else
            const bool detachCanvas = true;
#endif
            foreach (var lease in _canvases)
                lease.Promote(
                    lease.Canvas == controller.RootCanvasComponent
                        ? BppOverlaySorting.InspectionContent
                        : BppOverlaySorting.InspectionControls,
                    detach: detachCanvas
                );
            PromoteVariantControls();
#if BPP_ANDROID
            PlaceNativeTray();
#endif
            controller.SetCanvasInteractable(true);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Vector2 MeasureDescription(float width, float height)
    {
        if (_released || _controller == null)
            return Vector2.zero;
        CardRoot.sizeDelta = new Vector2(Screen.width, Screen.height);
#if BPP_ANDROID
        // The mobile drawer fits the description itself: the Scroll View viewport, its mask and the
        // tray backdrops all stay native, so nothing here may touch localScale or the layout.
        if (TryGetNativeTrayScreenRect(out var tray))
        {
            UpdateDrawerBackdrop(tray);
            return tray.size;
        }
        return FallbackTraySize;
#else
        var rect = _controller.TooltipRectTransform;
        rect.localScale = _tooltipRect.Scale;
        var bounds = DescriptionVisualBounds();
        var fit = NativeInspectionSizing.FitScale(bounds.width, bounds.height, width, height);
        rect.localScale *= fit;
        _descriptionScale = rect.localScale;
        return DescriptionVisualBounds().size;
#endif
    }

    public void PositionDescription(float centerX, float centerY, float scale)
    {
#if BPP_ANDROID
        // No-op: the native Scroll View owns where the description sits inside the drawer.
#else
        var rect = _controller.TooltipRectTransform;
        rect.localScale = _descriptionScale * scale;
        var bounds = DescriptionVisualBounds();
        rect.position += new Vector3(centerX - bounds.center.x, centerY - bounds.center.y, 0f);
#endif
    }

    /// <summary>
    /// Places the quality/enchantment selector directly under the description, centered on it.
    /// Returns the selector's screen rect so the inspection module can keep the pair on screen.
    ///
    /// The description is refitted whenever the paragraph changes size, so the band is recomputed
    /// from live visual bounds instead of a cached offset; a cached value would leave the chips
    /// overlapping the new text after a quality switch.
    /// </summary>
    public Rect PositionVariantBar(InspectionVariantBar? bar, float verticalGap)
    {
        if (bar == null || _released)
            return default;
#if BPP_ANDROID
        // The Android inspection places the selector under the enlarged card in the left half; the
        // description now lives inside the native drawer, so it is not the anchor any more.
        return default;
#else
        var description = DescriptionVisualBounds();
        var size = bar.Measure();
        var y = Mathf.Max(0f, description.yMin - verticalGap - size.y);
        bar.Root.position = new Vector3(description.center.x - size.x * .5f, y, 0f);
        return new Rect(description.center.x - size.x * .5f, y, size.x, size.y);
#endif
    }

    // Taps on the inspection overlay. Background dismissal stays a release gesture so dragging
    // never closes; a tap that lands on an interactive row opens the game's own nested tooltip.
    // Returns true when the tap was consumed, which keeps it from dismissing the inspection.
    public bool PollTap()
    {
        if (_released || !Application.isFocused)
            return false;
        var pointer = UnityEngine.InputSystem.Pointer.current;
        if (pointer == null)
            return false;
        var pressed = pointer.press.wasPressedThisFrame;
        var released = pointer.press.wasReleasedThisFrame;
        if (!pressed && !released)
            return false;
        var point = pointer.position.ReadValue();
        Vector2 origin;
#if BPP_ANDROID
        if (pressed && released && pointer is Touchscreen touchscreen)
        {
            // A quick tap can deliver both edges in one update. TouchControl keeps the down
            // position, so the travel check still works without a stored origin.
            origin = touchscreen.primaryTouch.startPosition.ReadValue();
            var quickTravel = (point - origin).magnitude;
            return quickTravel <= TapSlop && Tap(point);
        }
#endif
        if (pressed)
        {
            _tapOrigin = point;
            _tapFrame = Time.frameCount;
            return false;
        }
        // A drag is not a tap. Scrolling the description or pulling the card around must not
        // pop a tooltip when the finger lifts.
        var travel = (point - _tapOrigin).magnitude;
        if (_tapFrame == Time.frameCount || travel > TapSlop)
            return false;
        return Tap(point);
    }

    private bool Tap(Vector2 point)
    {
#if BPP_ANDROID
        // The tap that opened the inspection is still releasing on this frame, and the tooltip
        // content has only just been laid out. Never interpret either as a row tap.
        if (Time.frameCount <= _createdFrame)
            return false;
#endif
        var nested = NestedTooltips();
        if (nested == null)
            return false;
        // The selector owns its own region, so a tap there must never open a tooltip underneath it.
        if (IsVariantControlPoint(point))
        {
            if (!nested.IsOpen)
                return false;
            _backgroundPress.Reset();
            nested.Hide();
            return true;
        }
        if (!nested.ApplyTap(point))
            return false;
        // A consumed tap never reaches the dismissal sampler, so drop any press it is holding.
        _backgroundPress.Reset();
        return true;
    }

    /// <summary>Hides an open nested tooltip before the description is rebuilt.</summary>
    public void HideNestedTooltip() => NestedTooltips()?.Hide();

    private InspectionNestedTooltips? NestedTooltips()
    {
        if (_released || _controller == null)
            return null;
        _nestedTooltips ??= new InspectionNestedTooltips(_controller);
        return _nestedTooltips;
    }

    // Only the variant controls and their open lists protect clicks from dismissal.
    // Native fullscreen dropdown blockers do not protect clicks elsewhere.
    public bool PollDismiss()
    {
        var pointer = UnityEngine.InputSystem.Pointer.current;
        if (_released || pointer == null || !Application.isFocused)
        {
            _backgroundPress.Reset();
            return false;
        }
        var pressed = pointer.press.wasPressedThisFrame;
        var released = pointer.press.wasReleasedThisFrame;
        if (!pressed && !released)
            return false;
        Canvas.ForceUpdateCanvases();
#if BPP_ANDROID
        // A quick touch may deliver both edges in one Unity update. Unlike a mouse,
        // TouchControl retains its down position, so both ends can be checked safely.
        // Never dismiss with the release that created this inspection, and never dismiss a
        // gesture that belongs to the game's own drawer (it has to keep dragging/clicking).
        if (pressed && released && pointer is Touchscreen touchscreen)
        {
            _backgroundPress.Reset();
            var touch = touchscreen.primaryTouch;
            return Time.frameCount > _createdFrame
                && !IsVariantControlPoint(touch.startPosition.ReadValue())
                && !IsVariantControlPoint(touch.position.ReadValue())
                && !IsNativeTrayPoint(touch.startPosition.ReadValue())
                && !IsNativeTrayPoint(touch.position.ReadValue());
        }
#endif
        var point = pointer.position.ReadValue();
        return _backgroundPress.Sample(
            Time.frameCount,
            pressed,
            released,
            IsVariantControlPoint(point)
#if BPP_ANDROID
                || IsNativeTrayPoint(point)
#endif
        );
    }

    // Keep the hidden preview parent alive until the scope has settled pending SetUp calls.
    public void ReleaseNative()
    {
        if (_released)
            return;
        _released = true;
        try
        {
            _nestedTooltips?.Hide();
        }
        catch (Exception)
        {
            // The native tooltip hierarchy may already be torn down.
        }
        _nestedTooltips = null;
        // Restore descendants before parents; captured parents remain alive throughout.
        foreach (var lease in _canvases.AsEnumerable().Reverse())
            lease.Restore();
#if BPP_ANDROID
        if (_nativeTrayRect != null && _nativeTrayCaptured)
            _nativeTrayRect.anchoredPosition = _nativeTrayAnchoredPosition;
        _nativeTrayRect = null;
        _nativeTrayCaptured = false;
        if (_positioningAlphaCaptured && _controller != null)
            _controller.PositioningCanvasGroup.alpha = _positioningAlpha;
        _positioningAlphaCaptured = false;
#endif
        _tooltipRect.Restore();
        if (_nativeBackground != null)
        {
            _nativeBackground.enabled = _backgroundEnabled;
            _nativeBackground.raycastTarget = _backgroundRaycast;
        }
        _root.SetActive(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseNative();
        Object.Destroy(_root);
    }

    /// <summary>
    /// Creates the BPP-owned quality/enchantment selector above the inspection content.
    ///
    /// Native <c>CardLockVariantPanel</c> cannot be reused here: it is only built by
    /// <c>SetupLockVariantPanel</c>, whose eligibility guard needs a managed IL rewrite that has no
    /// counterpart on IL2CPP. Its dropdowns are also pointer-driven, which does not work with a
    /// fingertip. This bar renders in the same overlay canvas, so it is never behind the native
    /// tooltip hierarchy and its chips can be protected from the dismiss gesture.
    /// </summary>
    public InspectionVariantBar? CreateVariantBar(
        InspectionVariantOptions options,
        ETier selectedTier,
        EEnchantmentType? selectedEnchantment
    )
    {
        if (_released || _controller == null)
            return null;
        var bar = new InspectionVariantBar(_root.transform, options);
        bar.Populate(options, selectedTier, selectedEnchantment);
        // The description canvas sits above the backdrop and carries its own raycaster. A
        // transparent full-width graphic there would swallow chip taps, so give the selector its
        // own override-sorted canvas: chips stay the topmost interactive surface, exactly like the
        // native variant panel does on desktop.
        PromoteVariantBarCanvas(bar.Root);
        _protectedControls.Add(bar.Root);
        return bar;
    }

    private static void PromoteVariantBarCanvas(RectTransform root)
    {
        if (root.GetComponent<Canvas>() is not { } canvas)
            canvas = root.gameObject.AddComponent<Canvas>();
        canvas.overrideSorting = true;
        canvas.sortingOrder = BppOverlaySorting.InspectionVariantControls;
        if (root.GetComponent<GraphicRaycaster>() == null)
            root.gameObject.AddComponent<GraphicRaycaster>();
    }

    /// <summary>Stops protecting a selector that is about to be destroyed.</summary>
    public void ReleaseVariantBar(InspectionVariantBar? bar)
    {
        if (bar == null)
            return;
        _protectedControls.Remove(bar.Root);
        bar.Dispose();
    }

    private void PromoteVariantControls()
    {
        // The native panel is a separate subtree. Give it its own canvas/raycaster above
        // both description and dismiss background, regardless of its original camera canvas.
        var panelType = typeof(CardTooltipController).Assembly.GetType(
            "TheBazaar.UI.Tooltips.CardLockVariantPanel"
        );
        if (panelType == null)
            return;
        var panel = BppGameObjectFactory.GetComponent(_controller.LockModeContainer, panelType);
        var root =
            panel == null
                ? null
                : BppFieldAccessor.Find(panelType, "_panelRoot")?.GetValue<RectTransform>(panel);
        if (root == null)
            return;
        // Capture the concrete controls before any TMP dropdown creates a fullscreen
        // blocker. Never use the panel root or a global Selectable scan as a hit region.
        _controls.AddRange(root.GetComponentsInChildren<Selectable>(true));
        _dropdowns.AddRange(root.GetComponentsInChildren<TMP_Dropdown>(true));
        _captions.AddRange(root.GetComponentsInChildren<TMP_Text>(true));
        var canvas = root.GetComponent<Canvas>();
        var ownsCanvas = canvas == null;
        if (ownsCanvas)
            canvas = root.gameObject.AddComponent<Canvas>();
        var lease = new CanvasLease(canvas!, ownsCanvas);
        _canvases.Add(lease);
        lease.Promote(BppOverlaySorting.InspectionVariantControls, detach: false);
        Canvas.ForceUpdateCanvases();
    }

#if BPP_ANDROID
    /// <summary>
    /// Screen rectangle the game's own mobile drawer occupies (its <c>Scroll View</c>), converted
    /// through the tooltip canvas camera when that canvas is not a screen-space overlay.
    ///
    /// Everything the Android inspection lays out next to the drawer is derived from this rect, so
    /// a tablet, a phone or a re-scaled canvas never needs a hard-coded split.
    /// </summary>
    public bool TryGetNativeTrayScreenRect(out Rect rect)
    {
        rect = default;
        if (_released || _controller == null)
            return false;
        var tray = NativeTrayScrollRect();
        var trayRect = tray?.transform.AsRectTransform();
        if (trayRect == null)
            return false;
        rect = ToScreenRect(trayRect, _controller.RootCanvasComponent);
        return rect.width > 1f && rect.height > 1f;
    }

    /// <summary>Fallback drawer size while the native Scroll View is not measurable yet.</summary>
    private static Vector2 FallbackTraySize => new(Screen.width * .355f, Screen.height * .937f);

    private ScrollRect? NativeTrayScrollRect()
    {
        try
        {
            return _controller.MobileSecondaryTooltipScrollRect;
        }
        catch (Exception)
        {
            // A tooltip prefab without the mobile drawer has no such property value.
            return null;
        }
    }

    /// <summary>
    /// Snaps the drawer from its off-screen hide position to the position the native fade-in uses,
    /// so the first frame of the inspection already shows it.
    ///
    /// Native <c>MobileTooltipFadeAnimator.GetShowXPosition</c> returns <c>-rect.width</c> for the
    /// Scroll View's <c>anchoredPosition.x</c>; the disassembly of both that method and
    /// <c>CreateFadeIn</c> shows it writing that value while keeping the current Y.
    /// </summary>
    private void PlaceNativeTray()
    {
        var group = _controller.PositioningCanvasGroup;
        if (group != null)
        {
            _positioningAlpha = group.alpha;
            _positioningAlphaCaptured = true;
            group.alpha = 1f;
        }
        var tray = NativeTrayScrollRect();
        _nativeTrayRect = tray?.transform.AsRectTransform();
        if (_nativeTrayRect == null)
            return;
        _nativeTrayAnchoredPosition = _nativeTrayRect.anchoredPosition;
        _nativeTrayCaptured = true;
        // The drawer's width is a canvas-unit value, so it must be read from the native layout
        // instead of being derived from Screen.width while a CanvasScaler is active.
        Canvas.ForceUpdateCanvases();
        var width = _nativeTrayRect.rect.width;
        if (width > 1f)
        {
            var position = _nativeTrayAnchoredPosition;
            position.x = -width;
            _nativeTrayRect.anchoredPosition = position;
            if (tray?.content != null)
                tray.verticalNormalizedPosition = 1f;
            Canvas.ForceUpdateCanvases();
        }
        // Render order is the one part of the Android drawer that only the device can prove:
        // a camera-rendered tooltip canvas sits below BPP's overlay whatever its sort order says.
        var canvas = _controller.RootCanvasComponent;
        var trayScreen = ToScreenRect(_nativeTrayRect, canvas);
        UpdateDrawerBackdrop(trayScreen);
        if (canvas != null && trayScreen.width > 1f)
        {
            BppLog.InfoEvent(
                new BppLogEvent(
                    BppLogFeatureScope.CollectionPanel,
                    "collection_panel.inspection.android_drawer"
                ),
                ("render_mode", canvas.renderMode.ToString()),
                ("override_sorting", canvas.overrideSorting),
                ("sorting_order", canvas.sortingOrder),
                ("left", Mathf.RoundToInt(trayScreen.xMin)),
                ("width", Mathf.RoundToInt(trayScreen.width))
            );
        }
    }

    /// <summary>
    /// Pins the opaque plate to the game's own Scroll View rect so the collection's filter column
    /// can never be read through the description. The plate is a child of the BPP overlay canvas,
    /// which is 1:1 with screen pixels and sorts below the drawer's content canvas.
    /// </summary>
    private void UpdateDrawerBackdrop(Rect screenRect)
    {
        if (_drawerBackdrop == null || screenRect.width <= 1f || screenRect.height <= 1f)
            return;
        _drawerBackdrop.anchoredPosition = new Vector2(screenRect.xMin, screenRect.yMin);
        _drawerBackdrop.sizeDelta = screenRect.size;
    }

    private bool IsNativeTrayPoint(Vector2 point)
    {
        if (_released || _controller == null)
            return false;
        var trayRect = NativeTrayScrollRect()?.transform.AsRectTransform();
        if (trayRect == null)
            return false;
        var canvas = _controller.RootCanvasComponent;
        var camera =
            canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;
        return RectTransformUtility.RectangleContainsScreenPoint(trayRect, point, camera);
    }

    private static Rect ToScreenRect(RectTransform rect, Canvas? canvas)
    {
        var corners = new Vector3[4];
        rect.GetWorldCornersManaged(corners);
        var camera =
            canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;
        if (camera != null)
        {
            for (var i = 0; i < corners.Length; i++)
                corners[i] = RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
        }
        return Rect.MinMaxRect(
            corners.Min(p => p.x),
            corners.Min(p => p.y),
            corners.Max(p => p.x),
            corners.Max(p => p.y)
        );
    }
#endif

    private bool IsVariantControlPoint(Vector2 point)
    {
        if (
            _protectedControls.Any(rect =>
                rect != null
                && rect.gameObject.activeInHierarchy
                && ScreenBounds(rect).Contains(point)
            )
        )
            return true;        if (
            _controls.Any(control =>
                control != null
                && control.isActiveAndEnabled
                && control.targetGraphic != null
                && IsVisible(control.targetGraphic)
                && control.transform.AsRectTransform() is { } rect
                && ScreenBounds(rect).Contains(point)
            )
        )
            return true;
        if (
            _captions.Any(text =>
                text != null && IsVisible(text) && TextBounds(text).Contains(point)
            )
        )
            return true;
        return _dropdowns.Any(dropdown =>
            dropdown != null
            && DropdownList?.GetValue<GameObject>(dropdown) is { } list
            && list.activeInHierarchy
            && list.transform.AsRectTransform() is { } rect
            && ScreenBounds(rect).Contains(point)
        );
    }

    private Rect DescriptionVisualBounds()
    {
        var found = false;
        var bounds = default(Rect);
        foreach (
            var graphic in _controller.CanvasContentRectTransform.GetComponentsInChildren<Graphic>(
                false
            )
        )
        {
            if (!IsVisible(graphic))
                continue;
            var next = graphic.AsTmpText() is { } text
                ? TextBounds(text)
                : ScreenBounds(graphic.rectTransform);
            if (next.width <= 0f || next.height <= 0f)
                continue;
            bounds = found
                ? Rect.MinMaxRect(
                    Mathf.Min(bounds.xMin, next.xMin),
                    Mathf.Min(bounds.yMin, next.yMin),
                    Mathf.Max(bounds.xMax, next.xMax),
                    Mathf.Max(bounds.yMax, next.yMax)
                )
                : next;
            found = true;
        }
        return found ? bounds : ScreenBounds(_controller.CanvasContentRectTransform);
    }

    private static bool IsVisible(Graphic graphic) =>
        graphic.isActiveAndEnabled
        && graphic.color.a > .001f
        && !graphic.canvasRenderer.cull
        && graphic.canvasRenderer.GetInheritedAlpha() > .001f;

    private static Rect TextBounds(TMP_Text text)
    {
        var bounds = text.textBounds;
        var a = text.transform.TransformPoint(bounds.min);
        var b = text.transform.TransformPoint(bounds.max);
        return Rect.MinMaxRect(
            Mathf.Min(a.x, b.x),
            Mathf.Min(a.y, b.y),
            Mathf.Max(a.x, b.x),
            Mathf.Max(a.y, b.y)
        );
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }

    private static Rect ScreenBounds(RectTransform rect)
    {
        var corners = new Vector3[4];
        rect.GetWorldCornersManaged(corners);
        return Rect.MinMaxRect(
            corners.Min(p => p.x),
            corners.Min(p => p.y),
            corners.Max(p => p.x),
            corners.Max(p => p.y)
        );
    }

    private sealed class CanvasLease
    {
        public Canvas Canvas { get; }
        private readonly RectState _rect;
        private readonly RenderMode _mode;
        private readonly Camera? _camera;
        private readonly int _order;
        private readonly int _layer;
        private readonly bool _override;
        private readonly float _scale;
        private readonly CanvasScaler? _scaler;
        private readonly bool _scalerEnabled;
        private readonly (Canvas canvas, bool sorting, int order, int layer)[] _children;
        private readonly bool _ownsCanvas;
        private bool _detached;
        private readonly List<RaycasterLease> _raycasters = new();
        private readonly List<(
            CanvasGroup group,
            bool interactable,
            bool blocks,
            bool ignore,
            bool owned
        )> _groups = new();

        public CanvasLease(Canvas canvas, bool ownsCanvas = false)
        {
            Canvas = canvas;
            _ownsCanvas = ownsCanvas;
            _rect = new RectState(canvas.transform.RequireRectTransform());
            _mode = canvas.renderMode;
            _camera = canvas.worldCamera;
            _order = canvas.sortingOrder;
            _layer = canvas.sortingLayerID;
            _override = canvas.overrideSorting;
            _scale = canvas.scaleFactor;
            _scaler = canvas.GetComponent<CanvasScaler>();
            _scalerEnabled = _scaler != null && _scaler.enabled;
            _children = canvas
                .GetComponentsInChildren<Canvas>(true)
                .Where(c => c != canvas)
                .Select(c => (c, c.overrideSorting, c.sortingOrder, c.sortingLayerID))
                .ToArray();
        }

        public void Promote(int order, bool detach = true)
        {
            _detached = detach;
            if (detach)
            {
                Canvas.transform.SetParent(null, false);
                if (_scaler != null)
                    _scaler.enabled = false;
                Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                Canvas.scaleFactor = 1f;
                Canvas.transform.localScale = Vector3.one;
            }
            Canvas.overrideSorting = true;
            Canvas.sortingLayerID = 0;
            Canvas.sortingOrder = order;
            _raycasters.Add(new RaycasterLease(Canvas));
            foreach (var child in _children)
            {
                if (!child.canvas.transform.IsChildOf(Canvas.transform))
                    continue;
                child.canvas.overrideSorting = true;
                child.canvas.sortingOrder = order;
                child.canvas.sortingLayerID = 0;
                _raycasters.Add(new RaycasterLease(child.canvas));
            }
            if (!detach)
            {
                // A nested override canvas starts a new raycast-filter boundary. Keep its
                // controls interactive even if their former parent group blocked raycasts.
                var group = Canvas.GetComponent<CanvasGroup>();
                var ownsGroup = group == null;
                if (ownsGroup)
                    group = Canvas.gameObject.AddComponent<CanvasGroup>();
                _groups.Add(
                    (
                        group!,
                        group!.interactable,
                        group.blocksRaycasts,
                        group.ignoreParentGroups,
                        ownsGroup
                    )
                );
                group.interactable = group.blocksRaycasts = true;
                group.ignoreParentGroups = true;
            }
        }

        public void Restore()
        {
            if (Canvas == null)
                return;
            foreach (var child in _children)
            {
                if (child.canvas == null)
                    continue;
                child.canvas.overrideSorting = child.sorting;
                child.canvas.sortingOrder = child.order;
                child.canvas.sortingLayerID = child.layer;
            }
            foreach (var group in _groups)
            {
                if (group.group == null)
                    continue;
                group.group.interactable = group.interactable;
                group.group.blocksRaycasts = group.blocks;
                group.group.ignoreParentGroups = group.ignore;
                if (group.owned)
                    Object.Destroy(group.group);
            }
            foreach (var raycaster in _raycasters.AsEnumerable().Reverse())
                raycaster.Restore();
            // A lease that never detached its canvas has no transform, render mode or scaler state
            // to put back; re-parenting it here would move a canvas the game still owns.
            if (_detached)
            {
                _rect.Restore();
                Canvas.renderMode = _mode;
                Canvas.worldCamera = _camera;
                Canvas.scaleFactor = _scale;
                if (_scaler != null)
                    _scaler.enabled = _scalerEnabled;
            }
            Canvas.overrideSorting = _override;
            Canvas.sortingLayerID = _layer;
            Canvas.sortingOrder = _order;
            if (_ownsCanvas)
            {
                Canvas.enabled = false;
                Object.Destroy(Canvas);
            }
        }
    }

    private sealed class RaycasterLease
    {
        private readonly GraphicRaycaster _raycaster;
        private readonly bool _owned;
        private readonly bool _enabled;
        private readonly bool _reversed;
        private readonly GraphicRaycaster.BlockingObjects _blocking;

        public RaycasterLease(Canvas canvas)
        {
            var raycaster = canvas.GetComponent<GraphicRaycaster>();
            _owned = raycaster == null;
            _raycaster = raycaster ?? canvas.gameObject.AddComponent<GraphicRaycaster>();
            _enabled = _raycaster.enabled;
            _reversed = _raycaster.ignoreReversedGraphics;
            _blocking = _raycaster.blockingObjects;
            _raycaster.enabled = true;
            _raycaster.ignoreReversedGraphics = false;
            _raycaster.blockingObjects = GraphicRaycaster.BlockingObjects.None;
        }

        public void Restore()
        {
            if (_raycaster == null)
                return;
            _raycaster.enabled = !_owned && _enabled;
            _raycaster.ignoreReversedGraphics = _reversed;
            _raycaster.blockingObjects = _blocking;
            if (_owned)
                Object.Destroy(_raycaster);
        }
    }

    private sealed class RectState
    {
        private readonly RectTransform _rect;
        private readonly Transform? _parent;
        private readonly int _sibling;
        private readonly Vector2 _min,
            _max,
            _pivot,
            _size;
        private readonly Vector3 _position;
        private readonly Quaternion _rotation;
        public Vector3 Scale { get; }

        public RectState(RectTransform rect)
        {
            _rect = rect;
            _parent = rect.parent;
            _sibling = rect.GetSiblingIndex();
            _min = rect.anchorMin;
            _max = rect.anchorMax;
            _pivot = rect.pivot;
            _size = rect.sizeDelta;
            _position = rect.anchoredPosition3D;
            _rotation = rect.localRotation;
            Scale = rect.localScale;
        }

        public void Restore()
        {
            if (_rect == null)
                return;
            _rect.SetParent(_parent, false);
            _rect.SetSiblingIndex(_sibling);
            _rect.anchorMin = _min;
            _rect.anchorMax = _max;
            _rect.pivot = _pivot;
            _rect.sizeDelta = _size;
            _rect.anchoredPosition3D = _position;
            _rect.localScale = Scale;
            _rect.localRotation = _rotation;
        }
    }
}
