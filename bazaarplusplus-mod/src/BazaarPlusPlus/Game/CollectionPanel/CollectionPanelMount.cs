#nullable enable
using BazaarPlusPlus.Core.Events;
using BazaarPlusPlus.Core.Runtime;
using BazaarPlusPlus.Game.OverlayPanels;
using BazaarPlusPlus.GameInterop.CardPreview;
using BazaarPlusPlus.GameInterop.DayTiers;
using BazaarPlusPlus.GameInterop.StaticCards;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using UnityEngine;

namespace BazaarPlusPlus.Game.CollectionPanel;

// Custom mountable for CollectionPanel: ComponentMount<T> would attach the MonoBehaviour but
// could not subscribe to ChineseLocaleModeChanged, which the catalog cache + UI labels need
// in order to regenerate when the user cycles BPP's Chinese variant.
internal sealed class CollectionPanelMount : IBppMountable
{
    private readonly Func<OverlayPanelHost?> _overlayHost;
    private readonly BppStaticCardMapProvider _cardMapProvider;
    private readonly INativeCardPreviewHost _nativeCardPreviewHost;
    private readonly IGameDataDayTierResolver _dayTierResolver;
    private readonly CollectionInspectionModule _inspection;
    private IDisposable? _localeChangedSubscription;
    private CollectionPanel? _panel;

    public CollectionPanelMount(
        Func<OverlayPanelHost?> overlayHost,
        BppStaticCardMapProvider cardMapProvider,
        INativeCardPreviewHost nativeCardPreviewHost,
        IGameDataDayTierResolver dayTierResolver,
        CollectionInspectionModule inspection
    )
    {
        _overlayHost = overlayHost;
        _inspection = inspection;
        _cardMapProvider =
            cardMapProvider ?? throw new ArgumentNullException(nameof(cardMapProvider));
        _nativeCardPreviewHost =
            nativeCardPreviewHost ?? throw new ArgumentNullException(nameof(nativeCardPreviewHost));
        _dayTierResolver =
            dayTierResolver ?? throw new ArgumentNullException(nameof(dayTierResolver));
    }

    public void Mount(GameObject host, IBppServices services)
    {
        BppStartupTrace.Write("collection-panel:enter");
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        OverlayPanelHost? overlayHost;
        try
        {
            BppStartupTrace.Write("collection-panel:resolving-host");
            overlayHost = _overlayHost();
            BppStartupTrace.Write("collection-panel:host-resolved");
        }
        catch (Exception ex)
        {
            BppLog.ErrorEvent(
                new BppLogEvent(
                    BppLogFeatureScope.CollectionPanel,
                    "collection_panel.mount.failed"
                ),
                ex,
                ("reason_code", CollectionPanelLogReasonCode.OverlayHostUnavailable)
            );
            return;
        }
        if (overlayHost == null)
        {
            BppLog.ErrorEvent(
                new BppLogEvent(
                    BppLogFeatureScope.CollectionPanel,
                    "collection_panel.mount.failed"
                ),
                ("reason_code", CollectionPanelLogReasonCode.OverlayHostUnavailable)
            );
            return;
        }

        BppStartupTrace.Write("collection-panel:before-add-component");
        var panel = new CollectionPanel { Root = host.transform };
        _panel = panel;
        BppStartupTrace.Write("collection-panel:after-add-component");
        CollectionPanel.InitializeInstance(
            panel,
            services,
            _cardMapProvider,
            _nativeCardPreviewHost,
            _dayTierResolver,
            _inspection
        );
        panel.AttachToOverlayHost(overlayHost);
        BppStartupTrace.Write("collection-panel:initialized");

        BppStartupTrace.Write("collection-panel:attached");
        _localeChangedSubscription = services.EventBus.Subscribe<ChineseLocaleModeChanged>(_ =>
            CollectionPanel.NotifyLocaleChanged()
        );
    }

    public void Unmount(GameObject host)
    {
        _localeChangedSubscription?.Dispose();
        _localeChangedSubscription = null;

        _panel.Dispose();
        _panel = null;
    }
}
