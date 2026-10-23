#nullable enable
using BazaarPlusPlus.Game.HistoryPanel.AccountLink;
using BazaarPlusPlus.Game.HistoryPanel.Favorites;
using BazaarPlusPlus.Game.HistoryPanel.Storage;
using BazaarPlusPlus.GameInterop;
using BazaarPlusPlus.Infrastructure.Online;
using BazaarPlusPlus.ModApi.Clients;

namespace BazaarPlusPlus.Game.HistoryPanel;

internal sealed class HistoryPanelDependencies
{
    public HistoryPanelDependencies(
        IRunContext runContext,
        HistoryPanelDataService dataService,
        HistoryPanelReplayService replayService,
        ModApiSession? modApiSession,
        BazaarDbLinkClient? accountLinkClient,
        Func<AccountLinkGate>? accountLinkGate
    )
    {
        RunContext = runContext;
        DataService = dataService;
        ReplayService = replayService;
        ModApiSession = modApiSession;
        AccountLinkClient = accountLinkClient;
        AccountLinkGate = accountLinkGate;
    }

    public IRunContext RunContext { get; }

    public HistoryPanelDataService DataService { get; }

    public HistoryPanelReplayService ReplayService { get; }

    public ModApiSession? ModApiSession { get; }

    public BazaarDbLinkClient? AccountLinkClient { get; }

    public Func<AccountLinkGate>? AccountLinkGate { get; }

    /// <summary>
    /// Port transport policy for the panel's remaining direct online calls (health probe, account
    /// link redeem). Null on desktop, where the injected clients already carry a sane timeout and
    /// the callers keep their original behavior.
    /// </summary>
    public AndroidOnlineTransport? OnlineTransport { get; init; }

    public BazaarDbAccountLinkStore AccountLinkStore { get; init; } = new();

    // Null without a history database: the Favorites tab and the favorite action stay hidden.
    public HistoryFavoriteService? Favorites { get; init; }
}
