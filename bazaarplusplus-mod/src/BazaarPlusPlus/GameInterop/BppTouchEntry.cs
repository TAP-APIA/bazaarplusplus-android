#nullable enable
using System;
using BazaarPlusPlus.Game.CollectionPanel;
using BazaarPlusPlus.Game.HistoryPanel;
using BazaarPlusPlus.Game.LiveBuildPanel;
using BazaarPlusPlus.GameInterop.Fonts;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using BazaarPlusPlus.Infrastructure.UiTokens;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

namespace BazaarPlusPlus.GameInterop;

/// <summary>
/// On-screen touch floating dock for the Android port.
///
/// Renders a ScreenSpaceOverlay UGUI Canvas at sorting order 32000 so it is always visible above
/// both game canvases and BPP overlay panels. All helper methods that take delegates or non-Unity
/// types are static so <c>ClassInjector</c> never generates trampolines for them.
/// </summary>
internal sealed class BppTouchEntry : MonoBehaviour
{
    // Diagnostic probe (temporary): frames to wait after the dock canvas exists before the
    // one-shot uGUI raycast audit runs, so the lobby's native canvases are already alive.
    private const int RaycastAuditStartupDelayFrames = 180;
    private const int RaycastAuditPanelDelayFrames = 45;

    // Hold the BPP pill this long, then drag, to move the rail anywhere in the safe area.
    private const float DockLongPressSeconds = 0.45f;

    // The startup dump runs while the game is still on the loading/trailer screen, where the
    // main-menu controls do not exist yet. Probe once the lobby has actually been built, but
    // never poll forever: the window opens after the startup dump and closes after this timeout.
    // The window is wide because the dump must not fire on the loading screen, and a slow first
    // boot can sit there for minutes.
    private const float RaycastAuditMainMenuPollSeconds = 1f;
    private const float RaycastAuditMainMenuTimeoutSeconds = 300f;
    private const string MainMenuScalerOffsetPath = "Canvas/ScalerOffset";
    private const string MainMenuBackgroundName = "Image - BG";

    // The startup loading screen sits under the same Canvas/ScalerOffset path and was the control
    // the previous lobby dump actually measured, so its presence vetoes readiness outright.
    private const string MainMenuLoadingScreenName = "UI_LoadAppStartScreen_Dooley";

    // Positive evidence that the lobby is built, not an empty loader sharing the same Canvas path.
    // Direct children of Canvas/ScalerOffset are counted instead of the whole Canvas subtree: the
    // previous "Canvas carries >= 21 RectTransforms" test never opened on the device, and a direct
    // child count cannot be inflated by one deep but unbuilt panel.
    private const int MainMenuMinScalerChildCount = 8;

    // Every veto above may pass while the lobby still keeps fewer direct children than the
    // threshold, so the count is a fast path rather than the only path: after this many seconds of
    // an unchanged reading the gate opens anyway. Without the fallback a low-child lobby would
    // reproduce exactly the "no dump at all" failure this window exists to remove.
    private const float MainMenuChildCountFallbackSeconds = 10f;

    // The readiness rule, reported verbatim on a timeout so a device log says what was measured
    // instead of only that nothing happened.
    private const string MainMenuGateRule =
        "scaler_offset_active+no_loading_screen+background+trailer_silenced+direct_children>=8";

    // The opening trailer leaves its "skip" click layer behind: a full-screen Graphic on the
    // trailer's own root canvas (sorting order 0) whose alpha settled at 0 while its
    // raycastTarget stayed on, so it outranks every native screen's buttons in the EventSystem.
    // Poll it rarely and take away only that click target.
    private const float TrailerSkipProbeIntervalSeconds = 1f;
    private const float TrailerSkipInvisibleAlpha = 0.01f;
    private const int TrailerSkipSettleTicks = 2;
    private const int TrailerSkipUnprimedSettleTicks = 6;
    private const string TrailerRootName = "Trailer Component";
    private const string TrailerSkipClickAreaPath = "SkipCanvas/ScalerOffset/ClickArea";
    private const string TrailerSkipCanvasName = "SkipCanvas";
    private const string TrailerSkipDetachedEventId = "overlay_panels.trailer.skip_layer.detached";
    private const string TrailerSkipRevivedEventId =
        "overlay_panels.trailer.skip_layer.revived_while_silenced";

    private static int _nextPanel;

    // Where the dock starts before the user ever drags it: the safe right edge, vertically centred.
    private static Vector2 _initialAnchor = new(1f, .5f);
    private static Action<Vector2>? _persistAnchor;

    // Last reading of the lobby gate. Reported once if the window closes, so a timeout says which
    // condition was still false instead of leaving only "the audit never ran".
    private static string _mainMenuGateState = "not_observed";
    private static int _mainMenuGateChildCount;
    private static int _mainMenuGateActiveChildCount;
    private static float _mainMenuGateLowChildCountSince = -1f;
    private GameObject? _canvasRoot;
    private GameObject? _drawerRoot;
    private RectTransform? _dockRect;
    private bool _expanded = true;
    private bool _multiTouchLatch;
    private bool _fontApplied;

    // Dock drag state. A long press on the BPP pill starts a drag, which is the only way to move
    // the rail: taps still collapse and expand the drawer, so a drag sets _dockDragConsumedClick
    // to swallow the Button.onClick that release would otherwise deliver.
    private Vector2 _dockAnchor;
    private OverlayPixelRect _dockSafe;
    private Rect _toggleScreenRect;
    private bool _dockPressing;
    private bool _dockDragging;
    private bool _dockDragConsumedClick;
    private float _dockPressStartedAt;
    private int _dockDragTouchId = -1;
    private Vector2 _dockDragPointer;

    // Installed by Plugin before the canvas exists: the persisted anchor plus the callback that
    // writes it back once a drag ends.
    internal static void Install(Vector2 anchor, Action<Vector2> persist)
    {
        _initialAnchor = new Vector2(Mathf.Clamp01(anchor.x), Mathf.Clamp01(anchor.y));
        _persistAnchor = persist;
    }

    internal static void Reset()
    {
        _initialAnchor = new Vector2(1f, .5f);
        _persistAnchor = null;
    }

    private int _raycastAuditFrames;
    private bool _raycastAuditStartupDone;
    private int _raycastAuditPanelFrames;
    private bool _raycastAuditMainMenuDone;
    private float _raycastAuditMainMenuDeadline;
    private float _raycastAuditMainMenuNextProbeTime;

    // Trailer skip-layer probe state. The GameObject and Graphic are cached because
    // GameObject.Find walks the scene; the cache is dropped as soon as either is destroyed.
    private float _trailerSkipNextProbeTime;
    private GameObject? _trailerSkipRoot;
    private Graphic? _trailerSkipClickArea;
    private int _trailerSkipInvisibleTicks;
    private bool _trailerSkipSeenVisible;
    private bool _trailerSkipDetachLogged;
    private bool _trailerSkipRevivalLogged;

    private void OnEnable()
    {
        // Do not build UGUI/TMP objects during Chainloader startup (Plugin.Load): the game's first
        // scene and TMP resources are not initialized yet. Update() builds the dock once the native
        // font runtime is ready.
    }

    private void Update()
    {
        try
        {
            if (_canvasRoot == null)
            {
                if (TheBazaar.Localization.NotoFontFallbackRuntime._loadedSansPrimary != null)
                    EnsureCanvas();
            }
            else if (!_fontApplied)
            {
                TryApplyFont();
            }

            UpdateSafeArea();
            TickDockVisibility();
            PollDockDrag();
            PollThreeFingerGesture();
            TickRaycastAudit();
            TickTrailerSkipLayer();
        }
        catch (Exception ex)
        {
            BppStartupTrace.Write(
                "touch-entry:update-failed " + ex.GetType().Name + " | " + ex.Message
            );
        }
    }

    // Diagnostic probe (temporary): emits the uGUI raycast audit once after startup, once when
    // the game's main lobby is actually built, and once shortly after the build panel becomes
    // visible — the states that were measured to swallow taps. Pure observation; UiRaycastAudit
    // caps its own dump count and deduplicates triggers.
    private void TickRaycastAudit()
    {
        if (_canvasRoot == null)
            return;

        if (!_raycastAuditStartupDone)
        {
            _raycastAuditFrames++;
            if (_raycastAuditFrames < RaycastAuditStartupDelayFrames)
                return;

            _raycastAuditStartupDone = true;
            _raycastAuditMainMenuDeadline = Time.unscaledTime + RaycastAuditMainMenuTimeoutSeconds;
            UiRaycastAudit.DumpOnce("startup");
            return;
        }

        TickMainMenuAudit();

        if (!LiveBuildPanel.IsVisible)
        {
            _raycastAuditPanelFrames = 0;
            return;
        }

        if (_raycastAuditPanelFrames < int.MaxValue)
            _raycastAuditPanelFrames++;
        if (_raycastAuditPanelFrames == RaycastAuditPanelDelayFrames)
            UiRaycastAudit.DumpOnce("live_build_panel_visible");
    }

    // Polls the lobby gate once a second and dumps exactly once, so a dump taken on the loading
    // screen cannot happen. When the window closes without the gate ever opening, the last reading
    // is reported, which is what makes a timeout diagnosable.
    private void TickMainMenuAudit()
    {
        if (_raycastAuditMainMenuDone)
            return;

        var now = Time.unscaledTime;
        if (now >= _raycastAuditMainMenuDeadline)
        {
            _raycastAuditMainMenuDone = true;
            BppStartupTrace.Write("touch-entry:main-menu-audit-timeout gate=" + _mainMenuGateState);
            UiRaycastAudit.ReportMainMenuGateTimeout(
                _mainMenuGateState,
                _mainMenuGateChildCount,
                _mainMenuGateActiveChildCount,
                RaycastAuditMainMenuTimeoutSeconds
            );
            return;
        }

        if (now < _raycastAuditMainMenuNextProbeTime)
            return;
        _raycastAuditMainMenuNextProbeTime = now + RaycastAuditMainMenuPollSeconds;

        if (!IsMainMenuReady())
            return;

        _raycastAuditMainMenuDone = true;
        UiRaycastAudit.DumpOnce(UiRaycastAudit.MainMenuReadyTrigger);
    }

    // Every observation below must hold. The loading screen shares the Canvas/ScalerOffset path, so
    // a path-only test is what let the previous lobby dump measure the loader; the loading screen
    // marker and the direct-child count are what separate "lobby exists" from "lobby is built".
    // The gate writes the first failing condition to a static so a closed window can report it.
    //
    // The count is deliberately measured on the direct children of Canvas/ScalerOffset, not on the
    // whole Canvas subtree: the subtree threshold that used to live here never opened on the
    // device. It is also a fast path only — a lobby that passes all four vetoes but keeps fewer
    // children still opens the gate after MainMenuChildCountFallbackSeconds, so this probe cannot
    // become the next "the dump never ran" failure.
    private static bool IsMainMenuReady()
    {
        try
        {
            var scalerOffset = GameObject.Find(MainMenuScalerOffsetPath);
            if (scalerOffset == null || !scalerOffset.activeInHierarchy)
            {
                _mainMenuGateState = "no_scaler_offset";
                ResetLowChildCountWindow();
                return false;
            }

            // Transform.Find sees inactive children too, so activation is tested explicitly: a
            // present-but-inactive loader is not on screen and must not veto readiness.
            var loadingScreen = scalerOffset.transform.Find(MainMenuLoadingScreenName);
            if (loadingScreen != null && loadingScreen.gameObject.activeInHierarchy)
            {
                _mainMenuGateState = "loading_screen";
                ResetLowChildCountWindow();
                return false;
            }

            // A known child proves this is the lobby screen, not an empty loader that happens to
            // carry the same Canvas path.
            if (scalerOffset.transform.Find(MainMenuBackgroundName) == null)
            {
                _mainMenuGateState = "no_background";
                ResetLowChildCountWindow();
                return false;
            }

            // The trailer leaves a full-screen click layer behind, so the lobby is only interactive
            // once that layer can no longer take the raycast.
            if (!IsTrailerSkipLayerSilenced())
            {
                _mainMenuGateState = "trailer_layer_live";
                ResetLowChildCountWindow();
                return false;
            }

            var childCount = CountDirectChildren(scalerOffset.transform, out var activeChildCount);
            _mainMenuGateChildCount = childCount;
            _mainMenuGateActiveChildCount = activeChildCount;
            if (childCount >= MainMenuMinScalerChildCount)
            {
                ResetLowChildCountWindow();
                _mainMenuGateState = "ready";
                return true;
            }

            // Every veto passed and only the count is low: hold the same reading for a while, then
            // open rather than silently skipping the dump for the whole 300s window.
            var now = Time.unscaledTime;
            if (_mainMenuGateLowChildCountSince < 0f)
            {
                _mainMenuGateLowChildCountSince = now;
                _mainMenuGateState = "scaler_children_" + childCount;
                return false;
            }

            if (now - _mainMenuGateLowChildCountSince < MainMenuChildCountFallbackSeconds)
            {
                _mainMenuGateState = "scaler_children_" + childCount;
                return false;
            }

            _mainMenuGateState = "ready_child_count_fallback_" + childCount;
            return true;
        }
        catch
        {
            // A readiness probe must not disturb the frame it measures; retry on the next poll.
            _mainMenuGateState = "exception";
            return false;
        }
    }

    // Direct children only, walked by index: foreach over a Transform is the IL2CPP cast trap, and
    // a recursive count is what made the previous threshold unreadable on the device.
    private static int CountDirectChildren(Transform parent, out int activeChildCount)
    {
        activeChildCount = 0;
        var childCount = parent.childCount;
        for (var index = 0; index < childCount; index++)
        {
            var child = parent.GetChild(index);
            if (child != null && child.gameObject.activeSelf)
                activeChildCount++;
        }
        return childCount;
    }

    private static void ResetLowChildCountWindow() => _mainMenuGateLowChildCountSince = -1f;

    // The trailer root may already be gone (ready), or its SkipCanvas may have no Graphic and no
    // ClickArea (nothing can receive a raycast). While a live click layer remains with
    // raycastTarget on, the lobby is not considered interactive yet.
    private static bool IsTrailerSkipLayerSilenced()
    {
        var trailer = GameObject.Find(TrailerRootName);
        if (trailer == null)
            return true;

        var skipCanvas = trailer.transform.Find(TrailerSkipCanvasName);
        if (skipCanvas == null)
            return true;
        if (!skipCanvas.gameObject.activeInHierarchy)
            return true;

        var clickArea = skipCanvas.Find("ScalerOffset/ClickArea");
        var graphic =
            clickArea != null
                ? clickArea.GetComponent<Graphic>()
                : skipCanvas.GetComponent<Graphic>();
        if (graphic == null || !graphic.isActiveAndEnabled)
            return true;
        return !graphic.raycastTarget;
    }

    // Detaches the leftover trailer "skip" click layer once the trailer is over. The judgement is
    // deliberately one-sided: only a Graphic that is still a live raycast target, still active,
    // and has settled at zero alpha for consecutive one-second probes is touched, and only its
    // raycastTarget is cleared — the layer is never deactivated or destroyed, so the change stays
    // reversible. While the skip button is on screen its alpha is above zero, which is what keeps
    // this from touching a trailer that is still playing.
    private void TickTrailerSkipLayer()
    {
        var now = Time.unscaledTime;
        if (now < _trailerSkipNextProbeTime)
            return;
        _trailerSkipNextProbeTime = now + TrailerSkipProbeIntervalSeconds;

        var clickArea = ResolveTrailerSkipClickArea();
        if (clickArea == null)
        {
            _trailerSkipInvisibleTicks = 0;
            return;
        }

        // Already silenced: keep watching only to learn whether the trailer layer is ever visible
        // again while it is silenced, which would mean this changed the click layer too early. The
        // target is deliberately not restored — a trailer plays once, and the layer's job is done.
        if (!clickArea.raycastTarget)
        {
            _trailerSkipInvisibleTicks = 0;
            WatchSilencedTrailerSkipLayer(clickArea);
            return;
        }

        // Momentarily inactive: it cannot swallow a tap either, so leave it alone.
        if (!clickArea.isActiveAndEnabled)
        {
            _trailerSkipInvisibleTicks = 0;
            return;
        }

        if (!TryReadAlpha(clickArea, out var alpha) || alpha > TrailerSkipInvisibleAlpha)
        {
            // Visible, or unreadable: treat it as a trailer still on screen and do not touch it.
            _trailerSkipSeenVisible = true;
            _trailerSkipInvisibleTicks = 0;
            return;
        }

        // A fade-out crosses the threshold gradually, so require the same reading on consecutive
        // probes. A layer that was never seen visible may simply not have faded in yet, so it has
        // to hold zero for longer before it counts as finished.
        _trailerSkipInvisibleTicks++;
        var requiredTicks = _trailerSkipSeenVisible
            ? TrailerSkipSettleTicks
            : TrailerSkipUnprimedSettleTicks;
        if (_trailerSkipInvisibleTicks < requiredTicks)
            return;

        _trailerSkipInvisibleTicks = 0;
        try
        {
            clickArea.raycastTarget = false;
        }
        catch (Exception ex)
        {
            BppStartupTrace.Write(
                "touch-entry:trailer-skip-detach-failed " + ex.GetType().Name + " | " + ex.Message
            );
            return;
        }

        if (_trailerSkipDetachLogged)
            return;
        _trailerSkipDetachLogged = true;
        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.OverlayPanels, TrailerSkipDetachedEventId),
            ("canvas", TrailerSkipCanvasName),
            ("alpha", alpha)
        );
    }

    // Evidence-only: reports once if the silenced layer turns visible again, which would mean the
    // alpha test fired while the trailer was still showing. Never re-enables the raycast target.
    private void WatchSilencedTrailerSkipLayer(Graphic clickArea)
    {
        if (!_trailerSkipDetachLogged || _trailerSkipRevivalLogged)
            return;
        if (!TryReadAlpha(clickArea, out var alpha) || alpha <= TrailerSkipInvisibleAlpha)
            return;

        _trailerSkipRevivalLogged = true;
        BppLog.WarnEvent(
            new BppLogEvent(BppLogFeatureScope.OverlayPanels, TrailerSkipRevivedEventId),
            ("canvas", TrailerSkipCanvasName),
            ("alpha", alpha)
        );
    }

    private Graphic? ResolveTrailerSkipClickArea()
    {
        if (_trailerSkipClickArea != null)
            return _trailerSkipClickArea;

        try
        {
            if (_trailerSkipRoot == null)
                _trailerSkipRoot = GameObject.Find(TrailerRootName);
            if (_trailerSkipRoot == null)
                return null;

            var target = _trailerSkipRoot.transform.Find(TrailerSkipClickAreaPath);
            if (target == null)
                return null;

            // The click layer is an Image today; RawImage is accepted so a texture-backed layer
            // still resolves. Both expose Graphic.color and Graphic.raycastTarget.
            var graphic = target.GetComponent<Graphic>();
            if (graphic == null)
                return null;
            if (graphic.AsImage() == null && graphic.AsIl2Cpp<RawImage>() == null)
                return null;

            _trailerSkipClickArea = graphic;
            _trailerSkipSeenVisible = false;
            _trailerSkipInvisibleTicks = 0;
            return graphic;
        }
        catch (Exception ex)
        {
            // A diagnostic-grade probe must not disturb the frame it measures.
            BppStartupTrace.Write(
                "touch-entry:trailer-skip-probe-failed " + ex.GetType().Name + " | " + ex.Message
            );
            _trailerSkipRoot = null;
            _trailerSkipClickArea = null;
            return null;
        }
    }

    private static bool TryReadAlpha(Graphic graphic, out float alpha)
    {
        try
        {
            alpha = graphic.color.a;
            return true;
        }
        catch
        {
            alpha = 0f;
            return false;
        }
    }

    private void OnDestroy()
    {
        if (_canvasRoot != null)
            Destroy(_canvasRoot);
    }

    private void EnsureCanvas()
    {
        if (_canvasRoot != null)
            return;

        _canvasRoot = BppGameObjectFactory.Create(
            "BppTouchEntryCanvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster)
        );
        _canvasRoot.transform.SetParent(transform, false);

        var canvas = _canvasRoot.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000;

        var scaler = _canvasRoot.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;

        // Vertical rail placed by the persisted anchor, bottom-left anchored so a drag can move it
        // anywhere in the safe area. Defaults to the safe right edge, vertically centred.
        _dockAnchor = _initialAnchor;
        _dockRect = CreateRect(
            "BppTouchDock",
            _canvasRoot.transform,
            Vector2.zero,
            Vector2.zero,
            Vector2.zero,
            new Vector2(
                AndroidOverlayGeometry.DockColumnWidth,
                AndroidOverlayGeometry.DockColumnHeight
            )
        );
        var dockRect = _dockRect;
        var buttonSize = new Vector2(
            AndroidOverlayGeometry.DockColumnWidth,
            AndroidOverlayGeometry.ButtonHeight
        );
        var rowStep = AndroidOverlayGeometry.ButtonHeight + AndroidOverlayGeometry.DockGap;

        // Main toggle pill at the top of the rail.
        CreatePillButton(
            dockRect,
            "ToggleBtn",
            "BPP",
            Vector2.zero,
            buttonSize,
            new Color(0.14f, 0.11f, 0.08f, 0.92f),
            new Color(0.96f, 0.82f, 0.46f, 1f),
            ToggleDrawer
        );

        var drawerRect = CreateRect(
            "Drawer",
            dockRect,
            new Vector2(0f, 1f),
            new Vector2(0f, 1f),
            new Vector2(0f, -rowStep),
            new Vector2(
                AndroidOverlayGeometry.DockColumnWidth,
                rowStep * 3f - AndroidOverlayGeometry.DockGap
            )
        );
        _drawerRoot = drawerRect.gameObject;

        CreatePillButton(
            drawerRect,
            "BuildsBtn",
            "配装 Builds",
            Vector2.zero,
            buttonSize,
            new Color(0.18f, 0.14f, 0.09f, 0.92f),
            new Color(0.95f, 0.90f, 0.78f, 1f),
            static () =>
            {
                BppStartupTrace.Write("touch-entry:builds");
                LiveBuildPanel.ToggleFromUi();
            }
        );

        CreatePillButton(
            drawerRect,
            "CardsBtn",
            "图鉴 Cards",
            new Vector2(0f, -rowStep),
            buttonSize,
            new Color(0.18f, 0.14f, 0.09f, 0.92f),
            new Color(0.95f, 0.90f, 0.78f, 1f),
            static () =>
            {
                BppStartupTrace.Write("touch-entry:cards");
                CollectionPanel.ToggleFromUi();
            }
        );

        CreatePillButton(
            drawerRect,
            "HistoryBtn",
            "历史 History",
            new Vector2(0f, -rowStep * 2f),
            buttonSize,
            new Color(0.18f, 0.14f, 0.09f, 0.92f),
            new Color(0.95f, 0.90f, 0.78f, 1f),
            static () =>
            {
                BppStartupTrace.Write("touch-entry:history");
                HistoryPanel.ToggleFromUi();
            }
        );

        UpdateSafeArea();
        TryApplyFont();
        BppStartupTrace.Write("touch-entry:ugui-ready");
    }

    private void UpdateSafeArea()
    {
        if (_dockRect == null)
            return;

        // ReserveDockGutter only affects Content, never Safe, so reading the geometry here cannot
        // depend on the placement this method is about to compute.
        var geometry = AndroidOverlayViewport.Read();
        var scale = geometry.TouchScale;
        _dockSafe = geometry.Safe;
        _dockRect.localScale = new Vector3(scale, scale, 1f);
        var placement = AndroidOverlayGeometry.PlaceRail(
            geometry.Safe,
            _dockAnchor.x,
            _dockAnchor.y,
            _expanded,
            scale
        );
        _dockRect.anchoredPosition = new Vector2(placement.Left, placement.Bottom);
        _toggleScreenRect = new Rect(
            placement.Left,
            placement.Top - AndroidOverlayGeometry.ButtonHeight * scale,
            placement.Width,
            AndroidOverlayGeometry.ButtonHeight * scale
        );
    }

    // The dock exists to open a panel; while one is open its own close control is enough, and any
    // floating button over the card description covers the text the player is reading.
    private void TickDockVisibility()
    {
        if (_canvasRoot == null)
            return;

        var hide = CollectionPanel.IsVisible || LiveBuildPanel.IsVisible || HistoryPanel.IsVisible;
        if (hide == !_canvasRoot.activeSelf)
            return;

        if (hide && _dockPressing)
            EndDockDrag();
        _canvasRoot.SetActive(!hide);
    }

    // Long-press the BPP pill to move the rail. Input is polled instead of using uGUI drag
    // interfaces because interface dispatch on the injected MonoBehaviour needs Il2Cpp trampolines,
    // and the Button underneath already consumes the tap so the game never sees the press.
    private void PollDockDrag()
    {
        var touchscreen = Touchscreen.current;
        if (touchscreen == null)
        {
            _dockPressing = false;
            _dockDragging = false;
            return;
        }

        var touches = touchscreen.touches;
        if (!_dockPressing)
        {
            for (var i = 0; i < touches.Count; i++)
            {
                var candidate = touches[i];
                if (candidate == null || !candidate.press.wasPressedThisFrame)
                    continue;
                if (!candidate.press.isPressed)
                    continue;

                var position = candidate.position.ReadValue();
                if (!_toggleScreenRect.Contains(position))
                    continue;

                _dockPressing = true;
                _dockDragging = false;
                _dockDragConsumedClick = false;
                _dockPressStartedAt = Time.unscaledTime;
                _dockDragTouchId = candidate.touchId.ReadValue();
                _dockDragPointer = position;
                return;
            }

            return;
        }

        var touch = FindTouch(touches, _dockDragTouchId);
        if (touch == null)
        {
            EndDockDrag();
            return;
        }

        var current = touch.position.ReadValue();
        if (!_dockDragging)
        {
            // Only a finger that stays on the pill can pick the dock up; sliding off it first is a
            // swipe, not a move request.
            if (!_toggleScreenRect.Contains(current))
            {
                EndDockDrag();
                return;
            }

            if (Time.unscaledTime - _dockPressStartedAt >= DockLongPressSeconds)
            {
                _dockDragging = true;
                _dockDragConsumedClick = true;
            }
        }

        if (_dockDragging)
        {
            var delta = current - _dockDragPointer;
            var limit = _dockSafe.Width * .1f;
            if (delta.sqrMagnitude > limit * limit)
                delta = delta.normalized * limit;
            MoveDockAnchor(delta);
        }

        _dockDragPointer = current;
        if (touch.press.wasReleasedThisFrame || !touch.press.isPressed)
            EndDockDrag();
    }

    private static TouchControl? FindTouch(
        UnityEngine.InputSystem.Utilities.ReadOnlyArray<TouchControl> touches,
        int touchId
    )
    {
        for (var i = 0; i < touches.Count; i++)
        {
            var touch = touches[i];
            if (touch != null && touch.touchId.ReadValue() == touchId)
                return touch;
        }

        return null;
    }

    private void MoveDockAnchor(Vector2 deltaPixels)
    {
        if (_dockSafe.Width < 1f || _dockSafe.Height < 1f)
            return;

        _dockAnchor = new Vector2(
            Mathf.Clamp01(_dockAnchor.x + deltaPixels.x / _dockSafe.Width),
            Mathf.Clamp01(_dockAnchor.y + deltaPixels.y / _dockSafe.Height)
        );
    }

    private void EndDockDrag()
    {
        if (_dockDragging)
        {
            _persistAnchor?.Invoke(_dockAnchor);
            BppLog.InfoEvent(
                new BppLogEvent(BppLogFeatureScope.Plugin, "plugin.touch_dock.moved"),
                ("anchor_x", _dockAnchor.x.ToString("0.###")),
                ("anchor_y", _dockAnchor.y.ToString("0.###"))
            );
        }

        _dockPressing = false;
        _dockDragging = false;
        _dockDragTouchId = -1;
    }

    private void ToggleDrawer()
    {
        // A long-press drag ends with a click on the pill it started from; that click must not also
        // collapse the drawer. The flag clears on the next fresh press.
        if (_dockDragConsumedClick)
            return;

        _expanded = !_expanded;
        if (_drawerRoot != null)
            _drawerRoot.SetActive(_expanded);
        UpdateSafeArea();
    }

    private void TryApplyFont()
    {
        if (_canvasRoot == null)
            return;
        if (
            NativeGameTypography.PrepareOwnedText(out var prep)
                != NativeGameTypography.Outcome.Ready
            || prep == null
        )
            return;

        var labels = _canvasRoot.GetComponentsInChildren<TextMeshProUGUI>(true);
        if (labels == null || labels.Length == 0)
            return;

        for (var i = 0; i < labels.Length; i++)
        {
            if (labels[i] != null)
                prep.Apply(labels[i]);
        }
        _fontApplied = true;
    }

    private void PollThreeFingerGesture()
    {
        var touchscreen = Touchscreen.current;
        if (touchscreen == null)
            return;

        var pressed = 0;
        var touches = touchscreen.touches;
        for (var i = 0; i < touches.Count; i++)
        {
            if (touches[i].press.isPressed)
                pressed++;
        }

        if (pressed < 3)
        {
            _multiTouchLatch = false;
            return;
        }

        if (_multiTouchLatch)
            return;

        _multiTouchLatch = true;
        CycleNext();
    }

    private static RectTransform CreateRect(
        string name,
        Transform parent,
        Vector2 anchor,
        Vector2 pivot,
        Vector2 anchoredPosition,
        Vector2 sizeDelta
    )
    {
        var go = BppGameObjectFactory.Create(name, typeof(RectTransform));
        var rect = go.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = pivot;
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = sizeDelta;
        return rect;
    }

    // Dark pill with a gold frame. The dock floats above the panels, so the fill keeps the label
    // readable over whatever sits underneath instead of relying on the text alone.
    private static void CreatePillButton(
        Transform parent,
        string name,
        string title,
        Vector2 anchoredPos,
        Vector2 size,
        Color bgColor,
        Color textColor,
        Action onClick
    )
    {
        var borderRect = CreateRect(
            name,
            parent,
            new Vector2(0f, 1f),
            new Vector2(0f, 1f),
            anchoredPos,
            size
        );
        var borderImage = borderRect.gameObject.AddComponent<Image>();
        borderImage.color = new Color(0.82f, 0.66f, 0.32f, 0.90f);

        var innerRect = CreateRect(
            "Bg",
            borderRect,
            Vector2.zero,
            Vector2.zero,
            new Vector2(2f, 2f),
            size - new Vector2(4f, 4f)
        );
        var bgImage = innerRect.gameObject.AddComponent<Image>();
        bgImage.color = bgColor;

        var button = borderRect.gameObject.AddComponent<Button>();
        button.targetGraphic = bgImage;
        button.onClick.AddListener(onClick);

        var labelRect = CreateRect(
            "Label",
            innerRect,
            Vector2.zero,
            Vector2.zero,
            Vector2.zero,
            size - new Vector2(4f, 4f)
        );
        var label = labelRect.gameObject.AddComponent<TextMeshProUGUI>();
        label.text = title;
        label.fontSize = 18f;
        label.color = textColor;
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;
    }

    private static void CycleNext()
    {
        switch (_nextPanel % 3)
        {
            case 0:
                LiveBuildPanel.ToggleFromUi();
                break;
            case 1:
                CollectionPanel.ToggleFromUi();
                break;
            default:
                HistoryPanel.ToggleFromUi();
                break;
        }

        BppStartupTrace.Write("touch-entry:panel " + _nextPanel % 3);
        _nextPanel++;
    }
}
