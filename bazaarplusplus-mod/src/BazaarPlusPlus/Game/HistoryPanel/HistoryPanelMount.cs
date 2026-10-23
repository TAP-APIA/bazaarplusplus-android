#nullable enable
using BazaarPlusPlus.Core.Events;
using BazaarPlusPlus.Core.Runtime;
using BazaarPlusPlus.Game.CombatReplay;
using BazaarPlusPlus.Game.OverlayPanels;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using BazaarPlusPlus.Infrastructure.Online;
using BazaarPlusPlus.ModApi;
using BazaarPlusPlus.ModApi.Clients;
using BazaarPlusPlus.Storage.Paths;
using UnityEngine;

namespace BazaarPlusPlus.Game.HistoryPanel;

internal sealed class HistoryPanelMount : IBppMountable
{
    private readonly Func<CombatReplayRuntime?> _combatReplayRuntime;
    private readonly Func<ModApiSession?> _modApiSession;
    private readonly Func<BazaarDbLinkClient?> _accountLinkClient;
    private readonly Func<OverlayPanelHost?> _overlayHost;
    private IDisposable? _localeChangedSubscription;
    private OnlineSessionProvider? _ownedOnlineProvider;
    private HistoryPanel? _panel;

    public HistoryPanelMount(
        Func<CombatReplayRuntime?> combatReplayRuntime,
        Func<ModApiSession?> modApiSession,
        Func<BazaarDbLinkClient?> accountLinkClient,
        Func<OverlayPanelHost?> overlayHost
    )
    {
        _combatReplayRuntime = combatReplayRuntime;
        _modApiSession = modApiSession;
        _accountLinkClient = accountLinkClient;
        _overlayHost = overlayHost;
    }

    public void Mount(GameObject host, IBppServices services)
    {
        var combatReplayRuntime = _combatReplayRuntime();
        var overlayHost = _overlayHost();
        var modApiSession = _modApiSession();
        var accountLinkClient = _accountLinkClient();

        // The desktop entry point injects both clients. On the Android port that phase is still
        // skipped, so the panel would mount local-only and every online action would read as
        // "unavailable" — the very gap this block exists to close. Acquiring the same clients here
        // keeps enabling the entry point optional rather than a prerequisite.
#if BPP_ANDROID
        // Port-only: desktop always injects both (BuildOnlineServices), so this branch can never
        // fire there. Keeping it behind the define means a desktop build cannot silently switch
        // its online calls onto the port's handler shape.
        if (modApiSession == null || accountLinkClient == null)
        {
            AcquireOwnedOnlineClients(
                services.Config.BazaarDbUploadEnabled.Value,
                out var ownedSession,
                out var ownedLinkClient
            );
            modApiSession ??= ownedSession;
            accountLinkClient ??= ownedLinkClient;
        }
#endif

        var onlineTransport = _ownedOnlineProvider?.Transport;

        var plan = HistoryPanelMountPlan.Resolve(
            combatReplayRuntime != null,
            overlayHost != null,
            modApiSession != null
        );
        if (plan == HistoryPanelMountMode.DoNotMount && combatReplayRuntime == null)
        {
            LogMissingDependency(HistoryPanelMountDependency.CombatReplayRuntime);
            return;
        }

        if (plan == HistoryPanelMountMode.DoNotMount)
        {
            LogMissingDependency(HistoryPanelMountDependency.OverlayPanelHost);
            return;
        }

        var panel = new HistoryPanel { Root = host.transform };
        _panel = panel;

        // CombatReplayRuntime accessor is not a path and is not on services.Paths — pass it
        // straight through to Factory. Paths are startup-stable strings.
        panel.Configure(
            HistoryPanelFactory.Create(
                services.RunContext,
                modApiSession,
                () => combatReplayRuntime,
                PathConstants.RunLogDatabase(services.Paths.RequireDataRoot()),
                PathConstants.CombatReplays(services.Paths.RequireDataRoot()),
                PathConstants.CombatReplayVideos(services.Paths.RequireDataRoot()),
                accountLinkClient,
                () =>
                    HistoryPanelDecisions.ResolveAccountLinkGate(
                        services.Config.BazaarDbUploadEnabled.Value,
                        services.GameBuild.Channel
                    ),
                onlineTransport
            )
        );
        // Register with the host only once fully configured; an unconfigured panel (skip paths
        // above) must stay invisible to overlay lifecycle routing.
        panel.AttachToOverlayHost(overlayHost!);

        _localeChangedSubscription = services.EventBus.Subscribe<ChineseLocaleModeChanged>(_ =>
            HistoryPanel.RefreshLocalization()
        );
    }

    /// <summary>
    /// Builds the clients the entry point would have injected. Every failure is contained: a
    /// transport that cannot be constructed leaves the panel in <c>MountLocalOnly</c>, which keeps
    /// local runs, local replays and favorites fully working.
    /// </summary>
    private void AcquireOwnedOnlineClients(
        bool uploadsEnabled,
        out ModApiSession? modApiSession,
        out BazaarDbLinkClient? accountLinkClient
    )
    {
        modApiSession = null;
        accountLinkClient = null;
        try
        {
            var provider = new OnlineSessionProvider(
                new AndroidOnlineTransport(
                    TimeSpan.FromSeconds(ModApiUploadDefaults.RequestTimeoutSeconds)
                ),
                ModApiUploadDefaults.ApiBaseUrl,
                BppPluginVersion.Current
            );
            _ownedOnlineProvider = provider;

            modApiSession = provider.ResolveSession(injected: null);
            // The link client only exists when uploads are enabled — the same gate the
            // account-link card applies, so no socket pool is built for a hidden feature.
            if (uploadsEnabled)
                accountLinkClient = provider.ResolveLinkClient(injected: null);

            // Copied into locals first: out parameters cannot be captured by a lambda.
            var hasModApi = modApiSession != null;
            var hasAccountLink = accountLinkClient != null;
            BppLog.DebugEvent(
                new BppLogEvent(BppLogFeatureScope.HistoryPanel, "history_panel.online.acquired"),
                () => [("mod_api", hasModApi), ("account_link", hasAccountLink)]
            );
        }
        catch (Exception ex)
        {
            // A managed exception escaping Mount() would cross the IL2CPP boundary and terminate
            // the process, taking local History down with a feature it does not depend on.
            BppLog.ErrorEvent(
                new BppLogEvent(
                    BppLogFeatureScope.HistoryPanel,
                    "history_panel.online.acquire.failed",
                    storm: []
                ),
                ex,
                ("reason_code", HistoryPanelMountReasonCode.OnlineAcquireFailed)
            );
            _ownedOnlineProvider?.Dispose();
            _ownedOnlineProvider = null;
            modApiSession = null;
            accountLinkClient = null;
        }
    }

    private static void LogMissingDependency(HistoryPanelMountDependency dependency)
    {
        BppLog.ErrorEvent(
            new BppLogEvent(BppLogFeatureScope.HistoryPanel, "history_panel.mount.failed"),
            ("dependency", dependency),
            ("reason_code", HistoryPanelMountReasonCode.DependencyUnavailable)
        );
    }

    public void Unmount(GameObject host)
    {
        _localeChangedSubscription?.Dispose();
        _localeChangedSubscription = null;
        _panel?.Dispose();
        _panel = null;

        // The panel is gone before the clients it was using, so no in-flight continuation can
        // observe a disposed HttpClient.
        _ownedOnlineProvider?.Dispose();
        _ownedOnlineProvider = null;
    }
}
