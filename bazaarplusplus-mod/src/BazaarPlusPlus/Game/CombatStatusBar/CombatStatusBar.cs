#nullable enable
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Core.Runtime;
using TheBazaar;
using UnityEngine;

namespace BazaarPlusPlus.Game.CombatStatusBar;

internal sealed partial class CombatStatusBar : MonoBehaviour
{
    private static CombatStatusBar? _current;
    private static IBppServices? _services;
    private float _visualBlend;
    private int _screenshotSuppressionCount;

    private void Awake()
    {
        _current = this;
    }

    private void OnEnable()
    {
        Events.CombatStarted.AddListener(BppDelegates.Action(OnCombatStarted), this);
        Events.CombatEnded.AddListener(BppDelegates.Action(OnCombatEnded), this);
        EnsureConfigStateInitialized();
        EnsureUi();
        RefreshUi();
    }

    private void OnDisable()
    {
        Events.CombatStarted.RemoveListener(BppDelegates.Action(OnCombatStarted));
        Events.CombatEnded.RemoveListener(BppDelegates.Action(OnCombatEnded));
        SetUiVisible(false);
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(_current, this))
            _current = null;
        DisposeUi();
    }

    public void Initialize(IBppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    private void Update()
    {
        // A managed exception escaping a per-frame callback becomes std::terminate across the
        // IL2CPP boundary and kills the process with no managed stack.
        try
        {
            TickGuarded();
        }
        catch (Exception ex)
        {
            BppStartupTrace.Write("CombatStatusBar-update-exception:" + ex.GetType().FullName + " | " + ex.Message);
        }
    }

    private void TickGuarded()
    {
        EnsureConfigStateInitialized();

        _visualBlend = AdvanceVisualBlend(
            _visualBlend,
            IsCombatPlaybackActive,
            Time.unscaledDeltaTime
        );
    }

    private void LateUpdate()
    {
        EnsureUi();
        RefreshUi();
    }

    private bool ShouldDraw()
    {
        EnsureConfigStateInitialized();
        return _screenshotSuppressionCount == 0
            && ShouldRenderForState(_services?.Config.EnableCombatStatusBarConfig.Value == true);
    }

    internal static IDisposable? BeginScreenshotSuppression()
    {
        return _current?.BeginInstanceScreenshotSuppression();
    }

    private IDisposable BeginInstanceScreenshotSuppression()
    {
        _screenshotSuppressionCount++;
        SetUiVisible(false);
        return new ScreenshotSuppressionLease(this);
    }

    private void EndInstanceScreenshotSuppression()
    {
        if (_screenshotSuppressionCount > 0)
            _screenshotSuppressionCount--;

        RefreshUi();
    }

    private static void OnCombatStarted()
    {
        BeginCombatPlayback();
    }

    private static void OnCombatEnded()
    {
        EndCombatPlayback();
    }

    private sealed class ScreenshotSuppressionLease(CombatStatusBar owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            owner.EndInstanceScreenshotSuppression();
        }
    }
}
