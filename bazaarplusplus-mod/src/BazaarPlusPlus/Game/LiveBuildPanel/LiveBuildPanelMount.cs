#nullable enable

using BazaarPlusPlus.Core.Runtime;
using BazaarPlusPlus.Game.LiveBuildPanel.Recommendations;
using BazaarPlusPlus.Game.OverlayPanels;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using UnityEngine;

namespace BazaarPlusPlus.Game.LiveBuildPanel;

internal sealed class LiveBuildPanelMount : IBppMountable
{
    private readonly Func<OverlayPanelHost?> _overlayHost;
    private readonly BuildRecommendationRepository _recommendations;
    private LiveBuildPanel? _panel;

    public LiveBuildPanelMount(
        Func<OverlayPanelHost?> overlayHost,
        BuildRecommendationRepository recommendations
    )
    {
        _overlayHost = overlayHost;
        _recommendations = recommendations;
    }

    public void Mount(GameObject host, IBppServices services)
    {
        var overlayHost = _overlayHost();
        if (overlayHost == null)
        {
            BppLog.ErrorEvent(
                new BppLogEvent(BppLogFeatureScope.LiveBuildPanel, "live_build_panel.mount.failed"),
                ("reason_code", LiveBuildMountFailureReasonCode.OverlayHostUnavailable)
            );
            return;
        }

        var panel = new LiveBuildPanel { Root = host.transform };
        _panel = panel;
        panel.Initialize(_recommendations, overlayHost);
    }

    public void Unmount(GameObject host)
    {
        _panel.Dispose();
        _panel = null;
    }
}
