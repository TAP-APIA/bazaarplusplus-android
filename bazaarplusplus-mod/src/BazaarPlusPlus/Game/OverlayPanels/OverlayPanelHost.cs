#nullable enable
using BazaarPlusPlus.Game.Input;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using BazaarPlusPlus.Patches;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace BazaarPlusPlus.Game.OverlayPanels;

// The Overlay Panel Host (CONTEXT.md §Overlay panels): the single Unity adapter that reads
// frame inputs, runs the pure OverlayLifecycleCore rules, and dispatches open/close/tick
// callbacks to the registered Main Overlay Panels.
internal sealed class OverlayPanelHost : MonoBehaviour
{
    private readonly OverlayLifecycleCore _core = new();
    private readonly OverlayPanelHostLogState _logState = new();
    private readonly List<OverlayPanelRegistration> _registrations = new();
    private Scene _sceneTokenScene;
    private bool _sceneTokenLoaded;
    private string _sceneToken = string.Empty;

    public IOverlayPanelHandle Register(OverlayPanelRegistration registration)
    {
        if (registration == null)
            throw new ArgumentNullException(nameof(registration));

        _core.RegisterPanel(registration.PanelId, registration.SceneChangeClose);
        _registrations.Add(registration);
        return new PanelHandle(this, registration.PanelId);
    }

    private void Update()
    {
        // A managed exception escaping a per-frame callback crosses back into IL2CPP, where it
        // becomes std::terminate and kills the process with no managed stack. Containing it here
        // keeps a bad frame from taking the game down.
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            BppStartupTrace.Write(
                "overlay-update-exception:"
                    + ex.GetType().FullName
                    + " | "
                    + ex.Message
                    + " | "
                    + ex.StackTrace
            );
            BppLog.WarnEvent(
                new BppLogEvent(
                    BppLogFeatureScope.OverlayPanels,
                    "overlay_panels.update.degraded",
                    storm: ["reason_code"]
                ),
                ("reason_code", ex.GetType().Name)
            );
        }
    }

    private void Tick()
    {
        var keyboard = Keyboard.current;
        var modal = _core.OpenPanelId == null ? null : FindRegistration(_core.OpenPanelId);
        var inRunInspection =
            _core.OpenPanelId == null && BppPatchHost.Features.CollectionInspection.IsOpen;
        var modalActive = modal?.IsModalInputActive?.Invoke() == true || inRunInspection;
        // While a BPP panel or in-run card inspection owns the screen, Android back must close that view instead of
        // leaving the game. The bridge registers only for that window.
        SetBackConsume(_core.OpenPanelId != null || inRunInspection);
        var escapePressed = keyboard?.escapeKey.wasPressedThisFrame == true || TakeAndroidBack();
        if (inRunInspection && escapePressed)
        {
            BppPatchHost.Features.CollectionInspection.Close();
            escapePressed = false;
        }

        var snapshot = new OverlayFrameSnapshot
        {
            SceneToken = GetSceneToken(SceneManager.GetActiveScene()),
            IsInCombat = ReadIsInCombat(),
            EscapePressed = escapePressed,
            IsModalInputActive = modalActive,
            HotkeyPressedPanelId = modalActive ? null : ResolvePressedHotkeyPanelId(keyboard),
        };

        ExecuteDirectives(_core.Evaluate(snapshot));

        var dt = Time.unscaledDeltaTime;
        if (inRunInspection && BppPatchHost.Features.CollectionInspection.IsOpen)
            BppPatchHost.Features.CollectionInspection.Tick(dt);

        var openPanelId = _core.OpenPanelId;
        foreach (var registration in _registrations)
        {
            _logState.ExecuteTick(
                registration.PanelId,
                registration.Tick,
                dt,
                string.Equals(registration.PanelId, openPanelId, StringComparison.Ordinal)
            );
        }
    }

    private string? ResolvePressedHotkeyPanelId(Keyboard? keyboard)
    {
        foreach (var registration in _registrations)
        {
            if (
                !BppHotkeyService.WasToggleHotkeyPressedThisFrame(
                    registration.ToggleHotkey,
                    keyboard
                )
            )
                continue;

            // A failing guard swallows the whole toggle (open and close) for this frame.
            return registration.HotkeyGuard?.Invoke() == false ? null : registration.PanelId;
        }

        return null;
    }

    private void ExecuteDirectives(IReadOnlyList<OverlayDirective> directives)
    {
        foreach (var directive in directives)
        {
            var registration = FindRegistration(directive.PanelId);
            if (registration == null)
                continue;

            _logState.ExecuteDirective(
                Guid.NewGuid(),
                directive.PanelId,
                directive.Kind,
                () =>
                {
                    switch (directive.Kind)
                    {
                        case OverlayDirectiveKind.Close:
                            registration.OnClose();
                            break;
                        case OverlayDirectiveKind.NotifySceneChanged:
                            registration.OnSceneChanged?.Invoke();
                            break;
                        case OverlayDirectiveKind.Open:
                            registration.OnOpen();
                            break;
                        case OverlayDirectiveKind.DismissModal:
                            registration.DismissModal?.Invoke();
                            break;
                    }
                }
            );
        }
        SetBackConsume(_core.OpenPanelId != null);
    }

    private void OnDisable() => SetBackConsume(false);

    private OverlayPanelRegistration? FindRegistration(string panelId)
    {
        foreach (var registration in _registrations)
        {
            if (string.Equals(registration.PanelId, panelId, StringComparison.Ordinal))
                return registration;
        }

        return null;
    }

    private void Unregister(string panelId)
    {
        _core.UnregisterPanel(panelId);
        _logState.ForgetPanel(panelId);
        _registrations.RemoveAll(registration =>
            string.Equals(registration.PanelId, panelId, StringComparison.Ordinal)
        );
        SetBackConsume(_core.OpenPanelId != null);
    }

    private bool ReadIsInCombat() =>
        _logState.ReadIsInCombat(static () => TheBazaar.Data.IsInCombat);

    private static void SetBackConsume(bool consume)
    {
#if BPP_ANDROID
        BazaarPlusPlus.GameInterop.AndroidBackBridge.SetConsume(consume);
#endif
    }

    private static bool TakeAndroidBack()
    {
#if BPP_ANDROID
        return BazaarPlusPlus.GameInterop.AndroidBackBridge.TakeBackPress();
#else
        return false;
#endif
    }

    // Compare Scene values, not Scene.handle: Unity 6 changed that getter's return type
    // from int to SceneHandle. The equality operator's ABI is stable across both versions.
    private string GetSceneToken(Scene scene)
    {
#if BPP_ANDROID
        // Every Scene member this token would read (path, buildIndex, isLoaded, name) is an ICall
        // that Il2CppInterop could not resolve for the Android build; calling one throws
        // "ICall ... was not resolved" every frame inside Update. The token therefore reports a
        // fixed scene there, which means panels are not auto-closed on a scene change.
        return string.Empty;
#else
        if (
            _sceneToken.Length != 0
            && _sceneTokenScene == scene
            && _sceneTokenLoaded == scene.isLoaded
        )
            return _sceneToken;

        _sceneTokenScene = scene;
        _sceneTokenLoaded = scene.isLoaded;
        _sceneToken = $"{scene.name}|{scene.path}|{scene.buildIndex}|{scene.isLoaded}";
        return _sceneToken;
#endif
    }

    private sealed class PanelHandle : IOverlayPanelHandle
    {
        private readonly OverlayPanelHost _host;
        private readonly string _panelId;

        public PanelHandle(OverlayPanelHost host, string panelId)
        {
            _host = host;
            _panelId = panelId;
        }

        public bool IsVisible =>
            string.Equals(_host._core.OpenPanelId, _panelId, StringComparison.Ordinal);

        public OverlayRequestOutcome RequestOpen()
        {
            var outcome = _host._core.ExecuteOpenRequest(
                _panelId,
                _host.ReadIsInCombat(),
                out var directives
            );
            _host.ExecuteDirectives(directives);
            return outcome;
        }

        public OverlayRequestOutcome RequestClose()
        {
            var outcome = _host._core.ExecuteCloseRequest(_panelId, out var directives);
            _host.ExecuteDirectives(directives);
            return outcome;
        }

        public void Dispose() => _host.Unregister(_panelId);
    }
}
