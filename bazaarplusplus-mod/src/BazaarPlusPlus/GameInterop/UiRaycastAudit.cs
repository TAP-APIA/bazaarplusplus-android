#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BazaarPlusPlus.GameInterop;

/// <summary>
/// Temporary, observation-only audit of the uGUI raycast chain on Android.
///
/// The failure it answers: native UGUI never receives a tap while the BPP dock canvas
/// (sortingOrder 32000) and UI Toolkit both do, which points at a full-screen raycast target
/// sitting between the two orders. The audit enumerates every Canvas once, lists the
/// full-screen raycast-target Graphics registered to each one, and reports which Graphics win
/// a point test at the tap coordinates from the bug report.
///
/// It also measures the LiveBuildPanel buttons and the named native controls in screen pixels and
/// hit-tests each measured button at its own centre, so probe coordinates come from the controls
/// instead of from a hand-filled estimate that can miss the button entirely.
///
/// Read-only: it never writes to a Canvas, Graphic, CanvasGroup or EventSystem and never
/// dispatches an event. It runs a bounded number of times (see <see cref="DumpOnce"/>).
/// </summary>
internal static class UiRaycastAudit
{
    private const string AuditEventId = "overlay_panels.ui_raycast.audit";
    private const string ProbeEventId = "overlay_panels.ui_raycast.probe";
    private const string RectEventId = "overlay_panels.ui_raycast.rect";
    private const string GateEventId = "overlay_panels.ui_raycast.gate";
    private const string LiveBuildPanelRootName = "LiveBuildPanelView";
    private const int MaxGraphicsPerCanvas = 6;
    private const int MaxProbeHits = 6;
    private const int MaxSkipped = 8;
    private const int MaxControlRects = 24;
    private const int MaxNativeScanHits = 6;
    private const int MaxDumps = 4;
    private const int MaxPathDepth = 12;

    // The lobby dump is the one that can see the native "Continue" control. It is gated on the
    // trigger rather than on a name so the ordinary startup and build-panel dumps stay small.
    internal const string MainMenuReadyTrigger = "main_menu_ready";
    private const string MainCanvasRootName = "Canvas";
    private const int MaxMainMenuControlRects = 80;

    // The readiness rule BppTouchEntry enforces, restated here only so a timeout log carries the
    // rule that was measured instead of a bare state name.
    private const string MainMenuGateRule =
        "scaler_offset_active+no_loading_screen+background+trailer_silenced+direct_children>=8";
    private const int MainMenuMinScalerChildCount = 8;

    // Full RectTransform inventory of the native lobby Canvas. It deliberately applies no name and
    // no component filter: the native Continue button was missed by every name and type rule tried
    // so far, so the only admission tests left are geometric and raycast-based.
    private const string NativeAllRectControl = "native_all_rect";
    private const string NativeAllRectSummaryControl = "native_all_rect_summary";
    private const int MaxNativeAllRects = 200;
    private const int MinNativeAllRectWidth = 120;
    private const int MinNativeAllRectHeight = 40;

    // Insurance for the same hunt: the Continue control may be drawn by a Graphic whose
    // raycastTarget is off, or by a container with no Graphic at all and a clickable child. Those
    // fail the inventory's admission test by design, so the bottom-zone ones are listed separately
    // and bounded. A separate control value keeps the main inventory's meaning intact.
    private const string NativeAllRectShadowControl = "native_all_rect_noraycast";
    private const string NativeAllRectShadowSummaryControl = "native_all_rect_noraycast_summary";
    private const int MaxNativeAllRectShadowRows = 40;

    // `zone` buckets a control by where its tap centre lands, so one grep finds the band a caller
    // cares about. `bottom` is tested first because a low, wide action button stays "bottom" even
    // when it hugs the left or right edge.
    private const float ZoneBottomFraction = 0.65f;
    private const float ZoneTopFraction = 0.35f;
    private const float ZoneLeftFraction = 0.25f;
    private const float ZoneRightFraction = 0.75f;

    // Targeted probes for the "Continue" button, which the pixel search puts low on the screen: the
    // wide, flat, bottom-zone rects are the only plausible ones and each gets its own raycast.
    private const string BottomCandidateProbeControl = "bottom_candidate_probe";
    private const string BottomCandidateProbeSummaryControl = "bottom_candidate_probe_summary";
    private const int MaxBottomCandidates = 8;
    private const int MinBottomCandidateWidth = 150;
    private const int MaxBottomCandidateHeight = 200;

    // One-shot full-screen sweep for the lobby dump. Both y conventions are tried at every point,
    // because a wrong origin convention silently returns the full-screen background everywhere:
    // that is exactly how the earlier right-bottom-quadrant sweep read as "no buttons at all".
    private const int MainMenuGridStepPixels = 200;
    private const float MainMenuGridXMinFraction = 0.05f;
    private const float MainMenuGridXMaxFraction = 0.95f;
    private const float MainMenuGridYMinFraction = 0.05f;
    private const float MainMenuGridYMaxFraction = 0.95f;
    private const int MaxMainMenuGridNames = 60;

    // Calibration anchors. BPP's own dock buttons are known to answer a device tap at these screen
    // coordinates, so they prove the rect-to-tap conversion before any native coordinate is read
    // as clickable. The rule is the one the device taps were derived from.
    private const string CalibrationControl = "calibration_dock";
    private const string CalibrationSummaryControl = "calibration_dock_summary";
    private const string CalibrationTapControl = "calibration_tap";
    private const string CalibrationConvertRule = "screenY=H-worldY";
    private const string DockCanvasName = "BppTouchEntryCanvas";
    private const string DockContainerName = "BppTouchDock";
    private const string DrawerContainerName = "Drawer";
    private const float CalibrationTapY = 45f;
    private static readonly float[] CalibrationTapXs = { 2062f, 2290f, 2512f };
    private static readonly string[] DockButtonNames =
    {
        "ToggleBtn",
        "BuildsBtn",
        "CardsBtn",
        "HistoryBtn",
    };

    private static readonly string[] MainMenuControlNameTokens =
    {
        "continue",
        "play",
        "start",
        "resume",
    };

    // Fallback for a clickable that is neither a Selectable nor one of the known custom button
    // components. The raycastTarget check keeps decorative images out of the bounded list.
    private static readonly string[] MainMenuClickableNameTokens =
    {
        "btn",
        "button",
        "continue",
        "play",
    };

    // The native HUD controls the device taps came from. BPP does not own them, so a name is the
    // only handle available; every miss is reported as not_found instead of being skipped silently.
    private static readonly string[] NativeControlNames =
    {
        "Continue",
        "ContinueButton",
        "Continue Button",
        "Play",
        "PlayButton",
        "HomeContinue",
        "GoldPlus",
        "GoldPlusButton",
        "GoldAdd",
        "CurrencyPlus",
        "TopBarGoldPlus",
    };

    // A bounded name-token sweep over live Buttons: a native control named "Button_Continue" is
    // missed by GameObject.Find("Continue") but still turns up here, without guessing coordinates.
    private static readonly string[] NativeControlNameTokens = { "continue", "gold", "plus" };

    private static readonly Vector3[] Corners = new Vector3[4];
    private static readonly HashSet<string> DumpedTriggers = new(StringComparer.Ordinal);
    private static int _dumpCount;

    /// <summary>Dumps once per distinct trigger key; never more than <see cref="MaxDumps"/> times.</summary>
    internal static void DumpOnce(string trigger)
    {
        if (string.IsNullOrEmpty(trigger) || !DumpedTriggers.Add(trigger))
            return;
        if (_dumpCount >= MaxDumps)
            return;

        _dumpCount++;
        try
        {
            RunDump(trigger);
        }
        catch (Exception ex)
        {
            // A diagnostic must never disturb the frame it measures.
            try
            {
                BppLog.WarnEvent(
                    new BppLogEvent(
                        BppLogFeatureScope.OverlayPanels,
                        "overlay_panels.ui_raycast.degraded"
                    ),
                    ex,
                    ("trigger", trigger),
                    ("reason_code", ex.GetType().Name)
                );
            }
            catch
            {
                // Logging is best-effort here.
            }
        }
    }

    /// <summary>
    /// Reports, once, the gate reading that was still false when the lobby readiness window
    /// expired. Without it a timeout is indistinguishable from "the game never showed a lobby",
    /// and the evidence about which condition blocked the dump is gone.
    /// </summary>
    internal static void ReportMainMenuGateTimeout(
        string gateState,
        int scalerChildCount,
        int scalerActiveChildCount,
        float waitedSeconds
    )
    {
        try
        {
            BppLog.InfoEvent(
                new BppLogEvent(BppLogFeatureScope.OverlayPanels, GateEventId),
                ("trigger", MainMenuReadyTrigger),
                ("control", "main_menu_gate"),
                ("status", "timeout"),
                ("gate_state", gateState),
                ("gate_rule", MainMenuGateRule),
                ("scaler_child_count", scalerChildCount),
                ("scaler_active_child_count", scalerActiveChildCount),
                ("scaler_child_min", MainMenuMinScalerChildCount),
                ("waited_seconds", Mathf.RoundToInt(waitedSeconds)),
                ("convert_rule", CalibrationConvertRule),
                ("screen_width", Screen.width),
                ("screen_height", Screen.height)
            );
        }
        catch
        {
            // A diagnostic must never disturb the frame it measures.
        }
    }

    private static void RunDump(string trigger)
    {
        var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
        var graphics = UnityEngine.Object.FindObjectsOfType<Graphic>(true);
        var eventSystem = EventSystem.current;
        var module = ReadInputModule(eventSystem);

        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.OverlayPanels, AuditEventId),
            ("trigger", trigger),
            ("screen_width", Screen.width),
            ("screen_height", Screen.height),
            ("canvas_count", canvases.Length),
            ("graphic_count", graphics.Length),
            ("event_system", eventSystem == null ? "none" : eventSystem.name),
            ("event_system_active", eventSystem != null && eventSystem.isActiveAndEnabled),
            ("input_module", module == null ? "none" : module.GetType().Name),
            ("input_module_active", module != null && module.IsActive())
        );

        for (var index = 0; index < canvases.Length; index++)
        {
            var canvas = canvases[index];
            if (canvas == null)
                continue;
            EmitCanvas(canvas, graphics, trigger);
        }

        EmitRaycasters(trigger);
        ProbeAll(graphics, trigger);
        // Every dump carries the dock anchors: they are the only controls on this device with a
        // verified tap, so each dump re-proves the rect-to-tap conversion for its own screen state
        // (drawer expanded or collapsed, over the lobby or over a BPP panel).
        EmitDockCalibration(trigger);
        EmitControlRects(graphics, trigger);

        if (string.Equals(trigger, MainMenuReadyTrigger, StringComparison.Ordinal))
        {
            // Order matters only for readability: the unfiltered inventory comes first, because it
            // is the section that does not depend on any name or component guess.
            var bottomCandidates = EmitAllRectTransforms(trigger);
            EmitBottomCandidateProbes(trigger, bottomCandidates);
            EmitMainMenuControlRects(trigger);
            EmitMainMenuRaycastGrid(trigger);
        }
    }

    // Every BaseRaycaster that participates in EventSystem hit testing, which includes the
    // PanelRaycaster each runtime UI Toolkit panel registers. A UITK panel is invisible to the
    // Canvas sweep above and outranks uGUI by sortOrderPriority, so it must be listed too.
    private static void EmitRaycasters(string trigger)
    {
        var raycasters = UnityEngine.Object.FindObjectsOfType<BaseRaycaster>(true);
        for (var index = 0; index < raycasters.Length; index++)
        {
            var raycaster = raycasters[index];
            if (raycaster == null)
                continue;

            var canvas = raycaster.GetComponent<Canvas>();
            BppLog.InfoEvent(
                new BppLogEvent(BppLogFeatureScope.OverlayPanels, AuditEventId),
                ("trigger", trigger),
                ("canvas", canvas == null ? "ui_toolkit_panel" : canvas.name),
                ("path", PathOf(raycaster.transform, null)),
                ("raycaster_type", raycaster.GetType().Name),
                ("enabled", raycaster.enabled && raycaster.isActiveAndEnabled),
                ("sort_priority", raycaster.sortOrderPriority),
                ("render_priority", raycaster.renderOrderPriority),
                ("order", canvas == null ? -2 : canvas.sortingOrder)
            );
        }
    }

    private static BaseInputModule? ReadInputModule(EventSystem? eventSystem)
    {
        if (eventSystem == null)
            return null;
        try
        {
            return eventSystem.currentInputModule;
        }
        catch
        {
            return null;
        }
    }

    private static void EmitCanvas(Canvas canvas, Graphic[] graphics, string trigger)
    {
        var raycaster = canvas.GetComponent<GraphicRaycaster>();
        var raycasterState =
            raycaster == null ? "none"
            : raycaster.enabled && raycaster.isActiveAndEnabled ? "on"
            : "off";

        var blockers = new StringBuilder();
        var blockerCount = 0;
        for (var index = 0; index < graphics.Length; index++)
        {
            var graphic = graphics[index];
            if (!BelongsTo(graphic, canvas))
                continue;
            if (!IsFullScreenRaycastTarget(graphic))
                continue;

            blockerCount++;
            if (blockerCount > MaxGraphicsPerCanvas)
                continue;
            if (blockers.Length > 0)
                blockers.Append(',');
            blockers
                .Append(graphic.name)
                .Append('|')
                .Append(graphic.depth)
                .Append('|')
                .Append(AlphaPercent(graphic))
                .Append('|')
                .Append(IsBlockedByCanvasGroup(graphic.transform) ? "no_raycast" : "raycast");
        }

        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.OverlayPanels, AuditEventId),
            ("trigger", trigger),
            ("canvas", canvas.name),
            ("path", PathOf(canvas.transform, null)),
            ("order", canvas.sortingOrder),
            ("layer_id", canvas.sortingLayerID),
            ("mode", (int)canvas.renderMode),
            ("override", canvas.overrideSorting),
            ("root", canvas.rootCanvas == null ? "none" : canvas.rootCanvas.name),
            ("active", canvas.isActiveAndEnabled),
            ("raycaster", raycasterState),
            ("full_screen_blockers", blockerCount),
            ("blockers", blockers.ToString())
        );
    }

    // Unity's == compares native identity, which survives IL2CPP proxy re-wrapping;
    // ReferenceEquals would be false for two proxies of the same native Canvas.
    private static bool BelongsTo(Graphic? graphic, Canvas canvas) =>
        graphic != null && graphic.canvas != null && graphic.canvas == canvas;

    private static bool IsFullScreenRaycastTarget(Graphic graphic)
    {
        if (!graphic.raycastTarget || !graphic.isActiveAndEnabled || graphic.depth < 0)
            return false;
        if (graphic.canvasRenderer.cull)
            return false;
        if (!TryGetScreenRect(graphic.rectTransform, out var rect))
            return false;
        return rect.width >= Screen.width * 0.9f && rect.height >= Screen.height * 0.9f;
    }

    private static int AlphaPercent(Graphic graphic)
    {
        try
        {
            return Mathf.RoundToInt(Mathf.Clamp01(graphic.color.a) * 100f);
        }
        catch
        {
            return -1;
        }
    }

    private static void ProbeAll(Graphic[] graphics, string trigger)
    {
        if (Screen.width <= 0 || Screen.height <= 0)
            return;

        Probe("screen_center", Screen.width * 0.5f, Screen.height * 0.5f, graphics, trigger);
        // The two tap coordinates from the bug report. Skipped when the display is smaller.
        if (1858f < Screen.width && 1546f < Screen.height)
            Probe("resume_tap", 1858f, 1546f, graphics, trigger);
        if (1395f < Screen.width && 93f < Screen.height)
            Probe("gold_plus_tap", 1395f, 93f, graphics, trigger);

        // The Live Build panel's own buttons, at the coordinates measured on the device. The
        // former fractional corner (Screen.width * 0.93, Screen.height * 0.955) missed the close
        // button, so a "no button in hits" reading said nothing about the button being blocked.
        const float closeButtonX = 1969f;
        const float closeButtonY = 107f;
        const float refreshButtonX = 1963f;
        const float refreshButtonY = 226f;
        if (closeButtonX < Screen.width && closeButtonY < Screen.height)
            Probe("panel_close_corner", closeButtonX, closeButtonY, graphics, trigger);
        if (refreshButtonX < Screen.width && refreshButtonY < Screen.height)
            Probe("live_build_refresh_button", refreshButtonX, refreshButtonY, graphics, trigger);
        if (closeButtonX < Screen.width && closeButtonY < Screen.height)
            Probe("live_build_close_button", closeButtonX, closeButtonY, graphics, trigger);
    }

    private static void Probe(string label, float x, float y, Graphic[] graphics, string trigger)
    {
        var hits = new List<ProbeHit>(32);
        // Candidates the geometric sweep rejects although they cover the point. Without this the
        // log cannot tell "the button is not there" from "the button is there and unraycastable".
        var skipped = new StringBuilder();
        var skippedCount = 0;
        for (var index = 0; index < graphics.Length; index++)
        {
            var graphic = graphics[index];
            if (graphic == null || !TryGetScreenRect(graphic.rectTransform, out var rect))
                continue;
            if (x < rect.xMin || x > rect.xMax || y < rect.yMin || y > rect.yMax)
                continue;
            if (!IsLiveRaycastTarget(graphic))
            {
                skippedCount++;
                if (skippedCount > MaxSkipped)
                    continue;
                if (skipped.Length > 0)
                    skipped.Append(',');
                skipped
                    .Append(PathOf(graphic.transform, null))
                    .Append('|')
                    .Append(SkipReason(graphic));
                continue;
            }

            var canvas = graphic.canvas!;
            hits.Add(
                new ProbeHit(
                    canvas.sortingLayerID,
                    canvas.sortingOrder,
                    graphic.depth,
                    PathOf(graphic.transform, canvas.transform),
                    canvas.name,
                    graphic.raycastTarget
                )
            );
        }

        hits.Sort(static (left, right) => right.CompareTo(left));

        var top = new StringBuilder();
        var count = hits.Count < MaxProbeHits ? hits.Count : MaxProbeHits;
        for (var index = 0; index < count; index++)
        {
            if (top.Length > 0)
                top.Append(',');
            var hit = hits[index];
            top.Append(hit.Path)
                .Append('@')
                .Append(hit.Order)
                .Append('#')
                .Append(hit.Depth)
                .Append("|raycast=")
                .Append(hit.RaycastTarget ? "on" : "off");
        }

        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.OverlayPanels, ProbeEventId),
            ("trigger", trigger),
            ("probe", label),
            ("x", Mathf.RoundToInt(x)),
            ("y", Mathf.RoundToInt(y)),
            ("hit_count", hits.Count),
            ("top_hit", hits.Count == 0 ? "none" : hits[0].Path),
            ("top_canvas", hits.Count == 0 ? "none" : hits[0].CanvasName),
            ("top_order", hits.Count == 0 ? -1 : hits[0].Order),
            (
                "top_raycast",
                hits.Count == 0 ? "none"
                : hits[0].RaycastTarget ? "on"
                : "off"
            ),
            ("hits", top.ToString()),
            ("skipped_count", skippedCount),
            ("skipped", skipped.ToString())
        );

        EmitEventSystemProbe(label, x, y, trigger);
    }

    // Ground truth: the same call the input module makes, so a UI Toolkit PanelRaycaster or an
    // enabled module the geometric sweep cannot see still shows up here.
    private static void EmitEventSystemProbe(string label, float x, float y, string trigger)
    {
        var eventSystem = EventSystem.current;
        if (eventSystem == null || !eventSystem.isActiveAndEnabled)
            return;

        var hits = new StringBuilder();
        var hitCount = 0;
        var error = "none";
        try
        {
            var data = new PointerEventData(eventSystem) { position = new Vector2(x, y) };
#if BPP_ANDROID
            var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
#else
            var results = new List<RaycastResult>();
#endif
            eventSystem.RaycastAll(data, results);
            hitCount = results.Count;
            var limit = hitCount < MaxProbeHits ? hitCount : MaxProbeHits;
            for (var index = 0; index < limit; index++)
            {
                var hit = results[index];
                if (hits.Length > 0)
                    hits.Append(',');
                // The Graphics behind the hit carry raycastTarget; reporting it proves whether a
                // button reached the candidate list at all and with which setting.
                var graphic =
                    hit.gameObject == null ? null : hit.gameObject.GetComponent<Graphic>();
                hits.Append(hit.gameObject == null ? "null" : hit.gameObject.name)
                    .Append('@')
                    .Append(hit.sortingOrder)
                    .Append('#')
                    .Append(hit.depth)
                    .Append("|raycast=")
                    .Append(
                        graphic == null ? "no_graphic"
                        : graphic.raycastTarget ? "on"
                        : "off"
                    );
            }
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
        }

        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.OverlayPanels, ProbeEventId),
            ("trigger", trigger),
            ("probe", label),
            ("source", "event_system"),
            ("hit_count", hitCount),
            ("hits", hits.ToString()),
            ("error", error)
        );
    }

    // Hand-filled probe coordinates are why the previous round misread a working button: the pair
    // taken from the device sat nowhere near the close button, so "no button in hits" proved
    // nothing. This section measures the rectangles from the controls themselves instead, so the
    // next probe can use a point that is provably inside the button.
    //
    // Screen pixels equal world corners only while the owning Canvas renders in ScreenSpaceOverlay,
    // which both LiveBuildPanel and the native HUD do; `overlay` reports the case where that
    // assumption does not hold, so the numbers are never read as pixels without it.
    //
    // Each measured button also gets a hit test at its own centre, so the same log answers "is the
    // button hit-testable at its real position" instead of waiting for a hand-filled coordinate
    // that may miss it again.
    private static void EmitControlRects(Graphic[] graphics, string trigger)
    {
        var emitted = 0;
        var missing = 0;

        var buildPanel = GameObject.Find(LiveBuildPanelRootName);
        if (buildPanel == null)
        {
            EmitMissingRect(trigger, "live_build_root", LiveBuildPanelRootName);
            missing++;
        }
        else
        {
            EmitRect(trigger, "live_build_root", buildPanel.transform);
            emitted++;

            var layout = buildPanel.transform.Find("Panel");
            if (layout == null)
            {
                EmitMissingRect(trigger, "live_build_panel", "Panel");
                missing++;
            }
            else
            {
                // The panel-wide rect the Action buttons are compared against.
                EmitRect(trigger, "live_build_panel", layout);
                emitted++;
            }

            EmitLiveBuildActions(trigger, buildPanel.transform, graphics, ref emitted);
        }

        EmitNamedNativeControls(trigger, ref emitted, ref missing);

        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
            ("trigger", trigger),
            ("control", "summary"),
            ("rect_count", emitted),
            ("not_found_count", missing),
            ("unit", "screen_px"),
            ("screen_width", Screen.width),
            ("screen_height", Screen.height)
        );
    }

    // Calibration anchors. BPP's own dock is the only set of controls on this device with a
    // verified device tap, so it is the reference the conversion rule is checked against: the dump
    // prints the raw world corners and the tap coordinate derived from them side by side, then
    // names which dock button each known tap point lands in. A mismatch here means every other
    // coordinate in the same log is unusable, which is why it gets its own labelled rows.
    private static void EmitDockCalibration(string trigger)
    {
        var canvasObject = GameObject.Find(DockCanvasName);
        if (canvasObject == null)
        {
            BppLog.InfoEvent(
                new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
                ("trigger", trigger),
                ("control", CalibrationSummaryControl),
                ("status", "not_found"),
                ("count", 0),
                ("button_count", 0),
                ("dock_canvas", DockCanvasName),
                ("screen_height", Screen.height),
                ("convert_rule", CalibrationConvertRule)
            );
            return;
        }

        var emitted = 0;
        EmitRect(trigger, CalibrationControl, canvasObject.transform);
        emitted++;

        var dock = canvasObject.transform.Find(DockContainerName);
        if (dock == null)
        {
            EmitMissingRect(trigger, CalibrationControl, DockContainerName);
        }
        else
        {
            EmitRect(trigger, CalibrationControl, dock);
            emitted++;
        }

        // The drawer is a child of the dock and is toggled off when the drawer is collapsed. It is
        // still measured, because a collapsed drawer is what a tap at the dock would hit next.
        var drawer = dock == null ? null : dock.Find(DrawerContainerName);
        if (drawer == null)
        {
            EmitMissingRect(trigger, CalibrationControl, DrawerContainerName);
        }
        else
        {
            EmitRect(trigger, CalibrationControl, drawer);
            emitted++;
        }

        // Only components with a Button are clickable; that is exactly the set the device taps hit,
        // so no name filter is applied here and a future dock button is covered automatically.
        var buttons = canvasObject.GetComponentsInChildren<Button>(true);
        var buttonCount = buttons == null ? 0 : buttons.Length;
        for (var index = 0; index < buttonCount; index++)
        {
            var button = buttons![index];
            if (button == null)
                continue;
            EmitRect(trigger, CalibrationControl, button.transform);
            emitted++;
        }

        // Cross-check: each verified device tap is looked up against the tap rects derived above.
        // `matched_button` names the dock button the tap really lands in, so a conversion that is
        // off by a flip prints "none" on all three anchors instead of a coordinate to trust.
        var tapMatched = 0;
        var screenHeight = Screen.height;
        for (var tapIndex = 0; tapIndex < CalibrationTapXs.Length; tapIndex++)
        {
            var tapX = CalibrationTapXs[tapIndex];
            var onScreen =
                screenHeight > 0 && tapX < Screen.width && CalibrationTapY < screenHeight;
            var matchedButton = "none";
            var matchedWorldCenter = "none";
            if (onScreen)
            {
                for (var index = 0; index < buttonCount; index++)
                {
                    var button = buttons![index];
                    if (button == null)
                        continue;
                    var rect = button.transform.AsRectTransform();
                    if (rect == null || !TryGetScreenRect(rect, out var worldRect))
                        continue;
                    var tapRect = ToTapRect(worldRect);
                    if (
                        tapX < tapRect.xMin
                        || tapX > tapRect.xMax
                        || CalibrationTapY < tapRect.yMin
                        || CalibrationTapY > tapRect.yMax
                    )
                        continue;
                    matchedButton = button.name;
                    matchedWorldCenter = CenterText(worldRect);
                    break;
                }
            }
            if (matchedButton != "none")
                tapMatched++;

            BppLog.InfoEvent(
                new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
                ("trigger", trigger),
                ("control", CalibrationTapControl),
                ("tap_x", Mathf.RoundToInt(tapX)),
                ("tap_y", Mathf.RoundToInt(CalibrationTapY)),
                (
                    "tap_y_bottom_up",
                    screenHeight > 0 ? Mathf.RoundToInt(screenHeight - CalibrationTapY) : -1
                ),
                ("status", onScreen ? "ok" : "off_screen"),
                ("matched_button", matchedButton),
                ("matched_world_center", matchedWorldCenter),
                ("convert_rule", CalibrationConvertRule),
                ("screen_width", Screen.width),
                ("screen_height", screenHeight)
            );
        }

        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
            ("trigger", trigger),
            ("control", CalibrationSummaryControl),
            ("status", "ok"),
            ("count", emitted),
            ("button_count", buttonCount),
            ("dock_canvas", DockCanvasName),
            ("screen_height", Screen.height),
            ("screen_width", Screen.width),
            ("convert_rule", CalibrationConvertRule),
            ("tap_count", CalibrationTapXs.Length),
            ("tap_matched_count", tapMatched)
        );
    }

    // The lobby's own controls cannot be reached by name: the native Continue button is not a
    // UnityEngine.UI.Button and does not answer to any of NativeControlNames. This dump walks the
    // whole main Canvas instead and keeps only things that can plausibly be clicked, so its rects
    // are discoverable without guessing a name. Bounded to MaxMainMenuControlRects rows.
    private static void EmitMainMenuControlRects(string trigger)
    {
        var canvas = GameObject.Find(MainCanvasRootName);
        var rects = canvas == null ? null : canvas.GetComponentsInChildren<RectTransform>(true);
        if (rects == null)
        {
            BppLog.InfoEvent(
                new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
                ("trigger", trigger),
                ("control", "native_control_summary"),
                ("root", MainCanvasRootName),
                ("status", "not_found"),
                ("rect_transform_count", 0),
                ("matched_count", 0),
                ("rect_count", 0),
                ("truncated_count", 0)
            );
            return;
        }

        var matched = 0;
        var emitted = 0;
        var truncated = 0;
        var selectableCount = 0;
        var customCount = 0;
        var nameCount = 0;
        var raycastNameCount = 0;

        for (var index = 0; index < rects.Length; index++)
        {
            var rect = rects[index];
            if (rect == null || !TryClassifyMainMenuControl(rect, out var kind))
                continue;

            matched++;
            if (string.Equals(kind, "selectable", StringComparison.Ordinal))
                selectableCount++;
            else if (string.Equals(kind, "custom", StringComparison.Ordinal))
                customCount++;
            else if (string.Equals(kind, "name", StringComparison.Ordinal))
                nameCount++;
            else
                raycastNameCount++;

            if (emitted >= MaxMainMenuControlRects)
            {
                truncated++;
                continue;
            }

            EmitRect(trigger, "native_control", rect);
            emitted++;
        }

        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
            ("trigger", trigger),
            ("control", "native_control_summary"),
            ("root", MainCanvasRootName),
            ("status", "ok"),
            ("rect_transform_count", rects.Length),
            ("matched_count", matched),
            ("rect_count", emitted),
            ("truncated_count", truncated),
            ("selectable_count", selectableCount),
            ("custom_count", customCount),
            ("name_count", nameCount),
            ("raycast_name_count", raycastNameCount),
            ("rect_space", "world_corners"),
            ("screen_height", Screen.height)
        );
    }

    // Three independent reasons a RectTransform is worth reporting: it drives a Selectable, it
    // carries one of the game's custom button components, or its name says it is a control. The
    // last branch is the bounded fallback for a self-drawn Image + EventTrigger button.
    private static bool TryClassifyMainMenuControl(RectTransform rect, out string kind)
    {
        if (rect.GetComponent<Selectable>() != null)
        {
            kind = "selectable";
            return true;
        }

        if (
            rect.GetComponent<ButtonCustom>() != null
            || rect.GetComponent<BazaarButtonController>() != null
        )
        {
            kind = "custom";
            return true;
        }

        if (MatchesAnyToken(rect.name, MainMenuControlNameTokens))
        {
            kind = "name";
            return true;
        }

        var graphic = rect.GetComponent<Graphic>();
        if (
            graphic != null
            && graphic.raycastTarget
            && MatchesAnyToken(rect.name, MainMenuClickableNameTokens)
        )
        {
            kind = "raycast_name";
            return true;
        }

        kind = "none";
        return false;
    }

    private static bool MatchesAnyToken(string name, string[] tokens)
    {
        for (var index = 0; index < tokens.Length; index++)
        {
            if (name.IndexOf(tokens[index], StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    // The unfiltered inventory. It walks GameObjects.Find("Canvas") ->
    // GetComponentsInChildren<RectTransform>(true) and keeps every active, raycastable, at-least
    // button-sized rect without looking at a name or a component type, because the native Continue
    // control matched none of the rules tried so far. Each row carries the already-converted tap
    // rect, its centre, and a `zone` bucket derived from that centre, so the caller can grep
    // 'zone=bottom' instead of repeating the world-to-tap conversion outside the build.
    //
    // Bottom-zone rects that only fail the raycast test are emitted under the shadow control value
    // rather than dropped, because the control being hunted may draw itself without a raycast
    // target. Both branches feed the same bounded probe list.
    //
    // Returns the wide, flat, bottom-zone rects (at most MaxBottomCandidates, in emission order)
    // for the targeted raycast pass; nothing else consumes the list.
    private static List<BottomCandidate> EmitAllRectTransforms(string trigger)
    {
        var candidates = new List<BottomCandidate>(MaxBottomCandidates);
        var canvas = GameObject.Find(MainCanvasRootName);
        var rects = canvas == null ? null : canvas.GetComponentsInChildren<RectTransform>(true);
        if (rects == null)
        {
            EmitAllRectSummary(trigger, "not_found", 0, 0, 0, 0, 0, 0);
            return candidates;
        }

        var screenWidth = Screen.width;
        var screenHeight = Screen.height;
        var activeCount = 0;
        var eligibleCount = 0;
        var emitted = 0;
        var truncated = 0;
        var shadowEmitted = 0;
        var shadowTruncated = 0;

        for (var index = 0; index < rects.Length; index++)
        {
            var rect = rects[index];
            if (rect == null || !rect.gameObject.activeInHierarchy)
                continue;
            activeCount++;

            // Both the size test and the emitted tap rect come from the same world corners, so the
            // thresholds are compared in the pixels the device taps in rather than in local units.
            if (!TryGetScreenRect(rect, out var worldRect))
                continue;
            if (worldRect.width < MinNativeAllRectWidth || worldRect.height < MinNativeAllRectHeight)
                continue;

            // One tap-space rect feeds the zone bucket, the emitted row, and the probe list, so the
            // world-to-tap conversion happens exactly once per rect.
            var tapRect = ToTapRect(worldRect);
            var zone = ZoneOf(tapRect, screenWidth, screenHeight);

            var graphic = rect.GetComponent<Graphic>();
            if (graphic == null || !SafeRaycastTarget(graphic))
            {
                // Only the low band is kept: a layout container anywhere else is noise, while a
                // bottom-zone rect that merely lacks a raycast target may still be the button.
                if (!string.Equals(zone, "bottom", StringComparison.Ordinal))
                    continue;
                if (shadowEmitted >= MaxNativeAllRectShadowRows)
                {
                    shadowTruncated++;
                    continue;
                }
                shadowEmitted++;
                BppLog.InfoEvent(
                    new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
                    ("trigger", trigger),
                    ("control", NativeAllRectShadowControl),
                    ("name", rect.name),
                    ("path", PathOf(rect, null)),
                    ("tap_rect", TapRectText(worldRect)),
                    ("tap_center", TapCenterText(worldRect)),
                    ("size", SizeText(worldRect)),
                    ("raycast", "off"),
                    ("label", LabelOf(rect)),
                    ("sibling", rect.GetSiblingIndex()),
                    ("zone", zone),
                    ("reason", graphic == null ? "no_graphic" : "raycast_off")
                );
                TryAddBottomCandidate(
                    candidates,
                    rect,
                    tapRect,
                    worldRect.width,
                    worldRect.height
                );
                continue;
            }
            eligibleCount++;

            if (emitted >= MaxNativeAllRects)
            {
                truncated++;
                continue;
            }

            BppLog.InfoEvent(
                new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
                ("trigger", trigger),
                ("control", NativeAllRectControl),
                ("name", rect.name),
                ("path", PathOf(rect, null)),
                ("tap_rect", TapRectText(worldRect)),
                ("tap_center", TapCenterText(worldRect)),
                ("size", SizeText(worldRect)),
                ("raycast", "on"),
                ("label", LabelOf(rect)),
                ("sibling", rect.GetSiblingIndex()),
                ("zone", zone)
            );
            emitted++;

            if (string.Equals(zone, "bottom", StringComparison.Ordinal))
            {
                TryAddBottomCandidate(
                    candidates,
                    rect,
                    tapRect,
                    worldRect.width,
                    worldRect.height
                );
            }
        }

        EmitAllRectSummary(
            trigger,
            "ok",
            rects.Length,
            activeCount,
            eligibleCount,
            emitted,
            truncated,
            candidates.Count
        );

        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
            ("trigger", trigger),
            ("control", NativeAllRectShadowSummaryControl),
            ("root", MainCanvasRootName),
            ("status", "ok"),
            ("zone", "bottom"),
            ("rect_count", shadowEmitted),
            ("truncated_count", shadowTruncated),
            ("max_rows", MaxNativeAllRectShadowRows),
            ("reason_values", "no_graphic|raycast_off"),
            ("rect_space", "tap_screen_top_left"),
            ("screen_width", screenWidth),
            ("screen_height", screenHeight)
        );
        return candidates;
    }

    // Shared by both inventory branches: a bottom-zone rect that is wide enough and flat enough to
    // be an action button becomes a probe target, in emission order and up to the cap.
    private static void TryAddBottomCandidate(
        List<BottomCandidate> candidates,
        RectTransform rect,
        Rect tapRect,
        float width,
        float height
    )
    {
        if (candidates.Count >= MaxBottomCandidates)
            return;
        if (width < MinBottomCandidateWidth || height > MaxBottomCandidateHeight)
            return;
        candidates.Add(
            new BottomCandidate(
                rect.name,
                PathOf(rect, null),
                tapRect.center.x,
                tapRect.center.y,
                width,
                height
            )
        );
    }

    private static void EmitAllRectSummary(
        string trigger,
        string status,
        int rectTransformCount,
        int activeCount,
        int eligibleCount,
        int rectCount,
        int truncatedCount,
        int candidateCount
    )
    {
        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
            ("trigger", trigger),
            ("control", NativeAllRectSummaryControl),
            ("root", MainCanvasRootName),
            ("status", status),
            ("rect_transform_count", rectTransformCount),
            ("active_count", activeCount),
            ("eligible_count", eligibleCount),
            ("rect_count", rectCount),
            ("truncated_count", truncatedCount),
            ("candidate_count", candidateCount),
            ("min_width", MinNativeAllRectWidth),
            ("min_height", MinNativeAllRectHeight),
            ("max_rows", MaxNativeAllRects),
            ("zone_bottom_fraction", ZoneBottomFraction),
            ("zone_top_fraction", ZoneTopFraction),
            ("zone_left_fraction", ZoneLeftFraction),
            ("zone_right_fraction", ZoneRightFraction),
            ("rect_space", "tap_screen_top_left"),
            ("screen_width", Screen.width),
            ("screen_height", Screen.height)
        );
    }

    // The bands are tested in this order: bottom, top, left, right, centre. `bottom` wins first
    // because the button being hunted sits low and wide, and an edge-hugging action button must
    // still read as "bottom" when the caller greps one zone.
    private static string ZoneOf(Rect tapRect, int screenWidth, int screenHeight)
    {
        if (screenWidth <= 0 || screenHeight <= 0)
            return "center";
        if (tapRect.center.y > screenHeight * ZoneBottomFraction)
            return "bottom";
        if (tapRect.center.y < screenHeight * ZoneTopFraction)
            return "top";
        if (tapRect.center.x < screenWidth * ZoneLeftFraction)
            return "left";
        if (tapRect.center.x > screenWidth * ZoneRightFraction)
            return "right";
        return "center";
    }

    // raycastTarget lives on an IL2CPP proxy, and one unreadable flag must not abort the whole
    // inventory; an unreadable flag simply keeps the rect out of the clickable set.
    private static bool SafeRaycastTarget(Graphic graphic)
    {
        try
        {
            return graphic.raycastTarget;
        }
        catch
        {
            return false;
        }
    }

    // Each candidate is hit-tested at its own tap centre, passed to RaycastAll unchanged: the value
    // came out of the same `screenY=H-worldY` conversion the device taps were verified against, so
    // flipping it here would probe a mirrored point and answer a different question. The y actually
    // sent is printed as `tap_y` with `y_conversion=none`, so a wrong probe is readable as a number.
    private static void EmitBottomCandidateProbes(string trigger, List<BottomCandidate> candidates)
    {
        var eventSystem = EventSystem.current;
        var available = eventSystem != null && eventSystem.isActiveAndEnabled;
        if (!available || candidates.Count == 0)
        {
            BppLog.InfoEvent(
                new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
                ("trigger", trigger),
                ("control", BottomCandidateProbeSummaryControl),
                ("status", available ? "no_candidate" : "no_event_system"),
                ("candidate_count", candidates.Count),
                ("probe_count", 0),
                ("pick_rule", "zone=bottom+width>=150+height<=200"),
                ("raycast_space", "screen_top_left"),
                ("screen_width", Screen.width),
                ("screen_height", Screen.height)
            );
            return;
        }

        var probed = 0;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var hits = new StringBuilder(128);
            var hitCount = 0;
            var topHit = "none";
            var topPath = "none";
            var error = "none";
            try
            {
                var data = new PointerEventData(eventSystem!)
                {
                    position = new Vector2(candidate.TapX, candidate.TapY),
                };
#if BPP_ANDROID
                var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
#else
                var results = new List<RaycastResult>();
#endif
                eventSystem!.RaycastAll(data, results);
                hitCount = results.Count;
                var limit = hitCount < MaxProbeHits ? hitCount : MaxProbeHits;
                for (var hitIndex = 0; hitIndex < limit; hitIndex++)
                {
                    var hit = results[hitIndex];
                    var hitObject = hit.gameObject;
                    var hitName = hitObject == null ? "null" : hitObject.name;
                    if (hitIndex == 0)
                    {
                        topHit = hitName;
                        topPath = hitObject == null ? "none" : PathOf(hitObject.transform, null);
                    }
                    if (hits.Length > 0)
                        hits.Append(',');
                    hits.Append(hitName)
                        .Append('@')
                        .Append(hit.sortingOrder)
                        .Append('#')
                        .Append(hit.depth);
                }
            }
            catch (Exception ex)
            {
                // A probe must not disturb the frame it measures; the failure is reported instead.
                error = ex.GetType().Name;
            }

            probed++;
            BppLog.InfoEvent(
                new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
                ("trigger", trigger),
                ("control", BottomCandidateProbeControl),
                ("name", candidate.Name),
                ("path", candidate.Path),
                ("tap_x", Mathf.RoundToInt(candidate.TapX)),
                ("tap_y", Mathf.RoundToInt(candidate.TapY)),
                (
                    "size",
                    Mathf.RoundToInt(candidate.Width) + "," + Mathf.RoundToInt(candidate.Height)
                ),
                ("hit_count", hitCount),
                ("top_hit", topHit),
                ("top_path", topPath),
                ("hits", hits.Length == 0 ? "none" : hits.ToString()),
                ("raycast_space", "screen_top_left"),
                ("y_conversion", "none"),
                ("source", "event_system"),
                ("error", error)
            );
        }

        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
            ("trigger", trigger),
            ("control", BottomCandidateProbeSummaryControl),
            ("status", "ok"),
            ("candidate_count", candidates.Count),
            ("probe_count", probed),
            ("pick_rule", "zone=bottom+width>=150+height<=200"),
            ("max_candidates", MaxBottomCandidates),
            ("raycast_space", "screen_top_left"),
            ("screen_width", Screen.width),
            ("screen_height", Screen.height)
        );
    }

    // Two one-shot probes answer two separate questions, and both print their result because one
    // reading cannot tell "the control is not there" from "the probe measures the wrong space".
    //
    // Group A is the control group: the three screen coordinates that are known to activate BPP's
    // own dock buttons are raycast under both y conventions. Whichever convention returns a dock
    // button is the one EventSystem actually consumes; the other one is why the earlier quadrant
    // sweep reported nothing but the full-screen background.
    //
    // Group B is the discovery sweep: a coarse full-screen grid under both conventions, with a
    // per-convention hit count in the summary so one device run decides the convention. Every
    // unique hit is measured, so a native Continue button outside the main Canvas still leaves a
    // rect to tap.
    private static void EmitMainMenuRaycastGrid(string trigger)
    {
        var eventSystem = EventSystem.current;
        if (
            eventSystem == null
            || !eventSystem.isActiveAndEnabled
            || Screen.width <= 0
            || Screen.height <= 0
        )
        {
            EmitMainMenuRaycastSummary(trigger, 0, 0, 0, 0, 0, 0, false, "unavailable", "none");
            return;
        }

        var names = new StringBuilder();
        var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
        var pointCount = 0;
        var pointsWithLeftTopHit = 0;
        var pointsWithBottomUpHit = 0;
        var dockHitsLeftTop = 0;
        var dockHitsBottomUp = 0;
        var capReached = false;
        var error = "none";
        var leftTopText = new StringBuilder(128);
        var bottomUpText = new StringBuilder(128);
        // One managed list per convention, because the dedupe below needs the hit GameObject of
        // each point after both raycasts have run.
        var leftTopHits = new List<GridHit>(MaxProbeHits);
        var bottomUpHits = new List<GridHit>(MaxProbeHits);

        try
        {
            var data = new PointerEventData(eventSystem);

            // Group A: the calibration anchors, each under both conventions.
            for (var index = 0; index < CalibrationTapXs.Length; index++)
            {
                var tapX = CalibrationTapXs[index];
                if (tapX >= Screen.width || CalibrationTapY >= Screen.height)
                    continue;

                if (
                    EmitCalibrationRaycast(
                        trigger,
                        eventSystem,
                        data,
                        tapX,
                        CalibrationTapY,
                        "left_top",
                        leftTopText,
                        leftTopHits
                    )
                )
                    dockHitsLeftTop++;
                if (
                    EmitCalibrationRaycast(
                        trigger,
                        eventSystem,
                        data,
                        tapX,
                        Screen.height - CalibrationTapY,
                        "bottom_up",
                        bottomUpText,
                        bottomUpHits
                    )
                )
                    dockHitsBottomUp++;
            }

            // Group B: the full-screen grid, both conventions at every point.
            var minX = Mathf.CeilToInt(Screen.width * MainMenuGridXMinFraction);
            var maxX = Mathf.FloorToInt(Screen.width * MainMenuGridXMaxFraction);
            var minY = Mathf.CeilToInt(Screen.height * MainMenuGridYMinFraction);
            var maxY = Mathf.FloorToInt(Screen.height * MainMenuGridYMaxFraction);

            // Rows walk top-down in tap space so the log reads the way the screen looks.
            for (var y = minY; y <= maxY && !capReached; y += MainMenuGridStepPixels)
            {
                for (var x = minX; x <= maxX && !capReached; x += MainMenuGridStepPixels)
                {
                    pointCount++;
                    var leftTopCount = RaycastPoint(
                        eventSystem,
                        data,
                        x,
                        y,
                        "left_top",
                        leftTopText,
                        leftTopHits
                    );
                    var bottomUpCount = RaycastPoint(
                        eventSystem,
                        data,
                        x,
                        Screen.height - y,
                        "bottom_up",
                        bottomUpText,
                        bottomUpHits
                    );

                    if (leftTopCount > 0)
                        pointsWithLeftTopHit++;
                    if (bottomUpCount > 0)
                        pointsWithBottomUpHit++;

                    if (leftTopCount == 0 && bottomUpCount == 0)
                        continue;

                    BppLog.InfoEvent(
                        new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
                        ("trigger", trigger),
                        ("control", "native_control_grid_point"),
                        ("x", x),
                        ("y", y),
                        ("space", "screen_top_left"),
                        ("raycast_y_left_top", y),
                        ("raycast_y_bottom_up", Screen.height - y),
                        ("hits_with_y_left_top", leftTopCount),
                        ("hits_with_y_bottom_up", bottomUpCount),
                        (
                            "names_left_top",
                            leftTopText.Length == 0 ? "none" : leftTopText.ToString()
                        ),
                        (
                            "names_bottom_up",
                            bottomUpText.Length == 0 ? "none" : bottomUpText.ToString()
                        ),
                        ("source", "event_system")
                    );

                    capReached = CollectGridHits(
                        trigger,
                        x,
                        y,
                        leftTopHits,
                        bottomUpHits,
                        uniqueNames,
                        names
                    );
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
        }

        EmitMainMenuRaycastSummary(
            trigger,
            pointCount,
            pointsWithLeftTopHit,
            pointsWithBottomUpHit,
            dockHitsLeftTop,
            dockHitsBottomUp,
            uniqueNames.Count,
            capReached,
            error,
            names.ToString()
        );
    }

    // One anchor, one convention. The y actually sent to RaycastAll is reported verbatim, so the
    // convention that wins is readable as a number rather than as an interpretation.
    private static bool EmitCalibrationRaycast(
        string trigger,
        EventSystem eventSystem,
        PointerEventData data,
        float x,
        float raycastY,
        string space,
        StringBuilder text,
        List<GridHit> hits
    )
    {
        var hitCount = 0;
        var error = "none";
        var dockHit = false;
        var topName = "none";
        var topPath = "none";
        try
        {
            hitCount = RaycastPoint(eventSystem, data, x, raycastY, space, text, hits);
            for (var index = 0; index < hits.Count; index++)
            {
                var hit = hits[index];
                var hitObject = hit.Target;
                if (hitObject == null)
                    continue;
                if (index == 0)
                {
                    topName = hit.Name;
                    topPath = PathOf(hitObject.transform, null);
                }
                if (IsDockHit(hitObject))
                    dockHit = true;
            }
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
        }

        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
            ("trigger", trigger),
            ("control", "calibration_raycast"),
            ("space", space),
            ("tap_x", Mathf.RoundToInt(x)),
            ("tap_y_top_left", Mathf.RoundToInt(CalibrationTapY)),
            ("raycast_x", Mathf.RoundToInt(x)),
            ("raycast_y", Mathf.RoundToInt(raycastY)),
            ("hit_count", hitCount),
            ("top_hit", topName),
            ("top_path", topPath),
            ("dock_hit", dockHit ? "on" : "off"),
            ("hits", text.Length == 0 ? "none" : text.ToString()),
            ("error", error),
            ("screen_width", Screen.width),
            ("screen_height", Screen.height)
        );
        return dockHit;
    }

    // The single EventSystem call both groups share. `space` only labels the convention the caller
    // passed; this method never converts a coordinate, so a wrong convention stays visible instead
    // of being silently corrected.
    private static int RaycastPoint(
        EventSystem eventSystem,
        PointerEventData data,
        float x,
        float y,
        string space,
        StringBuilder text,
        List<GridHit> hits
    )
    {
        text.Clear();
        hits.Clear();
#if BPP_ANDROID
        var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
#else
        var results = new List<RaycastResult>();
#endif
        data.position = new Vector2(x, y);
        eventSystem.RaycastAll(data, results);
        var hitCount = results.Count;
        var limit = hitCount < MaxProbeHits ? hitCount : MaxProbeHits;
        for (var index = 0; index < limit; index++)
        {
            var hit = results[index];
            var hitObject = hit.gameObject;
            var hitName = hitObject == null ? "null" : hitObject.name;
            if (text.Length > 0)
                text.Append(',');
            text.Append(hitName).Append('@').Append(hit.sortingOrder).Append('#').Append(hit.depth);
            // The hierarchy path is what turns a hit name into a location, and it is the field that
            // separates two controls sharing a name in different canvases.
            if (hitObject != null)
                text.Append('|').Append(PathOf(hitObject.transform, null));
            hits.Add(new GridHit(hitName, hitObject, space));
        }
        return hitCount;
    }

    // First sighting of a name earns one deduplicated row plus the hit's own rect. Both
    // conventions feed this list, so a control reachable only under the other convention still
    // gets measured; `found_by` names the convention that found it. Returns true when the name
    // budget is spent, which stops the whole sweep.
    private static bool CollectGridHits(
        string trigger,
        int x,
        int y,
        List<GridHit> leftTopHits,
        List<GridHit> bottomUpHits,
        HashSet<string> uniqueNames,
        StringBuilder names
    )
    {
        for (var pass = 0; pass < 2; pass++)
        {
            var hits = pass == 0 ? leftTopHits : bottomUpHits;
            for (var index = 0; index < hits.Count; index++)
            {
                var hit = hits[index];
                var hitObject = hit.Target;
                if (hitObject == null || hit.Name == "null")
                    continue;
                if (uniqueNames.Contains(hit.Name))
                    continue;
                if (uniqueNames.Count >= MaxMainMenuGridNames)
                    return true;

                uniqueNames.Add(hit.Name);
                if (names.Length > 0)
                    names.Append(',');
                names.Append(hit.Name);

                BppLog.InfoEvent(
                    new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
                    ("trigger", trigger),
                    ("control", "native_control_grid_unique"),
                    ("name", hit.Name),
                    ("found_by", hit.Space),
                    ("x", x),
                    ("y", y),
                    ("space", "screen_top_left"),
                    ("source", "event_system")
                );

                // The hit may live outside the main Canvas. Measuring it here keeps the sweep
                // actionable instead of leaving only a name to chase next round.
                EmitRect(trigger, "native_control_grid_hit", hitObject.transform);
            }
        }
        return false;
    }

    private static bool IsDockHit(GameObject hitObject)
    {
        var name = hitObject.name;
        for (var index = 0; index < DockButtonNames.Length; index++)
        {
            if (string.Equals(name, DockButtonNames[index], StringComparison.Ordinal))
                return true;
        }
        return IsUnderDockCanvas(hitObject.transform);
    }

    private static bool IsUnderDockCanvas(Transform? transform)
    {
        var current = transform;
        var guard = 0;
        while (current != null && guard++ < MaxPathDepth)
        {
            if (string.Equals(current.name, DockCanvasName, StringComparison.Ordinal))
                return true;
            current = current.parent;
        }
        return false;
    }

    private static void EmitMainMenuRaycastSummary(
        string trigger,
        int pointCount,
        int pointsWithLeftTopHit,
        int pointsWithBottomUpHit,
        int dockHitsLeftTop,
        int dockHitsBottomUp,
        int uniqueNameCount,
        bool capReached,
        string error,
        string names
    )
    {
        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
            ("trigger", trigger),
            ("control", "native_control_grid_summary"),
            ("space", "screen_top_left"),
            ("x_min_fraction", MainMenuGridXMinFraction),
            ("x_max_fraction", MainMenuGridXMaxFraction),
            ("y_min_fraction", MainMenuGridYMinFraction),
            ("y_max_fraction", MainMenuGridYMaxFraction),
            ("step_px", MainMenuGridStepPixels),
            ("point_count", pointCount),
            ("hits_with_y_left_top", pointsWithLeftTopHit),
            ("hits_with_y_bottom_up", pointsWithBottomUpHit),
            ("calibration_dock_hits_left_top", dockHitsLeftTop),
            ("calibration_dock_hits_bottom_up", dockHitsBottomUp),
            ("unique_name_count", uniqueNameCount),
            ("truncated_count", capReached ? 1 : 0),
            ("error", error),
            ("names", names.Length == 0 ? "none" : names),
            ("convert_rule", CalibrationConvertRule),
            ("screen_width", Screen.width),
            ("screen_height", Screen.height)
        );
    }

    // Every LiveBuildPanel button is the "Action" container that Button() creates. The parent chain
    // separates Close from Refresh, and the Chevron rotation separates Previous from Next.
    private static void EmitLiveBuildActions(
        string trigger,
        Transform root,
        Graphic[] graphics,
        ref int emitted
    )
    {
        var rects = root.GetComponentsInChildren<RectTransform>(true);
        if (rects == null)
            return;

        var actionCount = 0;
        for (var index = 0; index < rects.Length; index++)
        {
            if (emitted >= MaxControlRects)
                return;
            var rect = rects[index];
            if (rect == null || !string.Equals(rect.name, "Action", StringComparison.Ordinal))
                continue;
            EmitRect(trigger, "live_build_action", rect);
            emitted++;
            actionCount++;
            if (TryGetScreenRect(rect, out var screenRect))
            {
                Probe(
                    "live_build_action_center_" + actionCount,
                    screenRect.center.x,
                    screenRect.center.y,
                    graphics,
                    trigger
                );
            }
        }
    }

    private static void EmitNamedNativeControls(string trigger, ref int emitted, ref int missing)
    {
        for (var index = 0; index < NativeControlNames.Length; index++)
        {
            if (emitted >= MaxControlRects)
                break;
            var name = NativeControlNames[index];
            var found = GameObject.Find(name);
            if (found == null)
            {
                EmitMissingRect(trigger, "native_name", name);
                missing++;
                continue;
            }
            EmitRect(trigger, "native_name", found.transform);
            emitted++;
        }

        EmitNativeNameScan(trigger, ref emitted);
    }

    private static void EmitNativeNameScan(string trigger, ref int emitted)
    {
        var buttons = UnityEngine.Object.FindObjectsOfType<Button>(true);
        if (buttons == null)
            return;

        var hits = 0;
        for (var index = 0; index < buttons.Length && hits < MaxNativeScanHits; index++)
        {
            if (emitted >= MaxControlRects)
                return;
            var button = buttons[index];
            if (button == null || !MatchesNativeToken(button.name))
                continue;
            EmitRect(trigger, "native_scan", button.transform);
            emitted++;
            hits++;
        }

        if (hits == 0)
            EmitMissingRect(trigger, "native_scan", "continue|gold|plus");
    }

    private static bool MatchesNativeToken(string name)
    {
        for (var index = 0; index < NativeControlNameTokens.Length; index++)
        {
            var token = NativeControlNameTokens[index];
            if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    private static void EmitRect(string trigger, string control, Transform? transform)
    {
        if (transform == null)
        {
            EmitMissingRect(trigger, control, "none");
            return;
        }

        var rectTransform = transform.AsRectTransform();
        if (rectTransform == null || !TryGetScreenRect(rectTransform, out var screenRect))
        {
            EmitMissingRect(trigger, control, transform.name);
            return;
        }

        var canvas = transform.GetComponentInParent<Canvas>();
        var overlay = canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay;
        var button = transform.GetComponent<Button>();

        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
            ("trigger", trigger),
            ("control", control),
            ("name", transform.name),
            ("path", PathOf(transform, null)),
            ("active", transform.gameObject.activeInHierarchy),
            ("rect", RectText(screenRect)),
            ("center", CenterText(screenRect)),
            ("size", SizeText(screenRect)),
            ("sibling", transform.GetSiblingIndex()),
            ("canvas", canvas == null ? "none" : canvas.name),
            ("overlay", overlay ? "on" : "off"),
            ("label", LabelOf(transform)),
            ("chevron_deg", ChevronDegrees(transform)),
            ("button", button == null ? "none" : "on"),
            (
                "interactable",
                button == null ? "none"
                : button.interactable ? "on"
                : "off"
            ),
            // The same rectangle in the space the device taps in, so the conversion is read here
            // instead of being repeated (and possibly reversed) outside the build.
            ("tap_center", TapCenterText(screenRect)),
            ("tap_rect", TapRectText(screenRect))
        );
    }

    // Same field set as EmitRect so one log grep sees a stable line shape; status carries the miss.
    private static void EmitMissingRect(string trigger, string control, string name)
    {
        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.OverlayPanels, RectEventId),
            ("trigger", trigger),
            ("control", control),
            ("name", name),
            ("path", "none"),
            ("active", false),
            ("rect", "none"),
            ("center", "none"),
            ("size", "none"),
            ("sibling", -1),
            ("canvas", "none"),
            ("overlay", "off"),
            ("label", "none"),
            ("chevron_deg", -1),
            ("button", "none"),
            ("interactable", "none"),
            ("tap_center", "none"),
            ("tap_rect", "none"),
            ("status", "not_found")
        );
    }

    // World corners grow up from the bottom edge; a device tap coordinate grows down from the top
    // edge. Screen.height is the only bridge between them, and it is 0 before the first frame, so
    // an unmeasurable frame is reported rather than converted.
    private static Rect ToTapRect(Rect worldRect)
    {
        var height = Screen.height;
        if (height <= 0)
            return default;
        return Rect.MinMaxRect(
            worldRect.xMin,
            height - worldRect.yMax,
            worldRect.xMax,
            height - worldRect.yMin
        );
    }

    private static string TapRectText(Rect worldRect)
    {
        if (Screen.height <= 0)
            return "none";
        return RectText(ToTapRect(worldRect));
    }

    private static string TapCenterText(Rect worldRect)
    {
        if (Screen.height <= 0)
            return "none";
        return CenterText(ToTapRect(worldRect));
    }

    private static string RectText(Rect rect) =>
        $"{Mathf.RoundToInt(rect.xMin)},{Mathf.RoundToInt(rect.yMin)},"
        + $"{Mathf.RoundToInt(rect.xMax)},{Mathf.RoundToInt(rect.yMax)}";

    private static string CenterText(Rect rect) =>
        $"{Mathf.RoundToInt(rect.center.x)},{Mathf.RoundToInt(rect.center.y)}";

    private static string SizeText(Rect rect) =>
        $"{Mathf.RoundToInt(rect.width)},{Mathf.RoundToInt(rect.height)}";

    private static string LabelOf(Transform transform)
    {
        var texts = transform.GetComponentsInChildren<TMP_Text>(true);
        if (texts != null)
        {
            for (var index = 0; index < texts.Length; index++)
            {
                var text = texts[index];
                if (text == null)
                    continue;
                var value = text.text;
                if (!string.IsNullOrEmpty(value))
                    return Flatten(value);
            }
        }

        // Some lobby controls still use legacy uGUI Text; without this fallback their label
        // would read "none" even though the visible caption is the control's identity.
        var legacyTexts = transform.GetComponentsInChildren<Text>(true);
        if (legacyTexts == null)
            return "none";
        for (var index = 0; index < legacyTexts.Length; index++)
        {
            var text = legacyTexts[index];
            if (text == null)
                continue;
            var value = text.text;
            if (!string.IsNullOrEmpty(value))
                return Flatten(value);
        }
        return "none";
    }

    // Log fields are comma-separated, so a control's own text must not inject separators.
    private static string Flatten(string value)
    {
        var limit = value.Length < 24 ? value.Length : 24;
        var builder = new StringBuilder(limit);
        for (var index = 0; index < limit; index++)
        {
            var ch = value[index];
            builder.Append(ch is ',' or '|' or '\n' or '\r' or ' ' ? '_' : ch);
        }
        return builder.Length == 0 ? "none" : builder.ToString();
    }

    private static int ChevronDegrees(Transform transform)
    {
        var chevron = transform.Find("Chevron");
        return chevron == null ? -1 : Mathf.RoundToInt(chevron.localEulerAngles.z);
    }

    // Names the first rule that kept a covering Graphic out of the candidate list, so a device log
    // can say "the button is in the tree but raycastTarget is off" instead of "no hit".
    private static string SkipReason(Graphic? graphic)
    {
        if (graphic == null)
            return "null";
        if (!graphic.isActiveAndEnabled)
            return "inactive";
        if (!graphic.raycastTarget)
            return "raycastTarget=false";
        if (graphic.depth < 0)
            return "depth<0";
        if (graphic.canvasRenderer.cull)
            return "culled";
        var canvas = graphic.canvas;
        if (canvas == null)
            return "no_canvas";
        if (!canvas.isActiveAndEnabled)
            return "canvas_off";
        var raycaster = canvas.GetComponent<GraphicRaycaster>();
        if (raycaster == null)
            return "no_raycaster";
        if (!raycaster.enabled || !raycaster.isActiveAndEnabled)
            return "raycaster_off";
        if (IsBlockedByCanvasGroup(graphic.transform))
            return "canvasgroup";
        return "unknown";
    }

    private static bool IsLiveRaycastTarget(Graphic? graphic)
    {
        if (graphic == null || !graphic.raycastTarget || !graphic.isActiveAndEnabled)
            return false;
        if (graphic.depth < 0 || graphic.canvasRenderer.cull)
            return false;
        var canvas = graphic.canvas;
        if (canvas == null || !canvas.isActiveAndEnabled)
            return false;
        var raycaster = canvas.GetComponent<GraphicRaycaster>();
        if (raycaster == null || !raycaster.enabled || !raycaster.isActiveAndEnabled)
            return false;
        return !IsBlockedByCanvasGroup(graphic.transform);
    }

    private static bool TryGetScreenRect(RectTransform? rect, out Rect screenRect)
    {
        screenRect = default;
        if (rect == null)
            return false;

        rect.GetWorldCornersManaged(Corners);
        var minX = Mathf.Min(
            Mathf.Min(Corners[0].x, Corners[1].x),
            Mathf.Min(Corners[2].x, Corners[3].x)
        );
        var maxX = Mathf.Max(
            Mathf.Max(Corners[0].x, Corners[1].x),
            Mathf.Max(Corners[2].x, Corners[3].x)
        );
        var minY = Mathf.Min(
            Mathf.Min(Corners[0].y, Corners[1].y),
            Mathf.Min(Corners[2].y, Corners[3].y)
        );
        var maxY = Mathf.Max(
            Mathf.Max(Corners[0].y, Corners[1].y),
            Mathf.Max(Corners[2].y, Corners[3].y)
        );
        if (maxX <= minX || maxY <= minY)
            return false;

        screenRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
        return true;
    }

    // Mirrors uGUI's rule: the nearest CanvasGroup with blocksRaycasts = false stops the
    // pointer, and ignoreParentGroups ends the walk.
    private static bool IsBlockedByCanvasGroup(Transform? transform)
    {
        var current = transform;
        var guard = 0;
        while (current != null && guard++ < 32)
        {
            var groups = current.GetComponents<CanvasGroup>();
            for (var index = 0; index < groups.Length; index++)
            {
                var group = groups[index];
                if (group == null)
                    continue;
                if (!group.blocksRaycasts)
                    return true;
                if (group.ignoreParentGroups)
                    return false;
            }
            current = current.parent;
        }
        return false;
    }

    private static string PathOf(Transform? transform, Transform? stopAt)
    {
        if (transform == null)
            return "none";

        var parts = new List<string>(6);
        var current = transform;
        var guard = 0;
        while (current != null && guard++ < MaxPathDepth)
        {
            parts.Add(current.name);
            if (stopAt != null && current == stopAt)
                break;
            current = current.parent;
        }

        var builder = new StringBuilder();
        for (var index = parts.Count - 1; index >= 0; index--)
        {
            if (builder.Length > 0)
                builder.Append('/');
            builder.Append(parts[index]);
        }
        return builder.ToString();
    }

    // One bottom-zone rect worth hit-testing, kept with the already-converted tap centre so the
    // probe loop performs no conversion of its own.
    private readonly struct BottomCandidate
    {
        internal BottomCandidate(
            string name,
            string path,
            float tapX,
            float tapY,
            float width,
            float height
        )
        {
            Name = name;
            Path = path;
            TapX = tapX;
            TapY = tapY;
            Width = width;
            Height = height;
        }

        internal string Name { get; }
        internal string Path { get; }
        internal float TapX { get; }
        internal float TapY { get; }
        internal float Width { get; }
        internal float Height { get; }
    }

    // One EventSystem hit, kept alongside the rendered text so the dedupe can still measure the
    // control it names. The GameObject is only read to fetch a Transform, never held across frames.
    private readonly struct GridHit
    {
        internal GridHit(string name, GameObject? target, string space)
        {
            Name = name;
            Target = target;
            Space = space;
        }

        internal string Name { get; }
        internal GameObject? Target { get; }

        // The coordinate convention the caller passed to RaycastAll when this hit was produced.
        internal string Space { get; }
    }

    private readonly struct ProbeHit : IComparable<ProbeHit>
    {
        internal ProbeHit(
            int layerId,
            int order,
            int depth,
            string path,
            string canvasName,
            bool raycastTarget
        )
        {
            LayerId = layerId;
            Order = order;
            Depth = depth;
            Path = path;
            CanvasName = canvasName;
            RaycastTarget = raycastTarget;
        }

        // A sorting layer id is a hash, not an ordering value, so the report ranks by sorting
        // order first and prints the id for context.
        internal int LayerId { get; }
        internal int Order { get; }
        internal int Depth { get; }
        internal string Path { get; }
        internal string CanvasName { get; }
        internal bool RaycastTarget { get; }

        // Negative when this hit draws above the other one.
        public int CompareTo(ProbeHit other)
        {
            if (Order != other.Order)
                return Order > other.Order ? -1 : 1;
            if (Depth != other.Depth)
                return Depth > other.Depth ? -1 : 1;
            return 0;
        }
    }
}
