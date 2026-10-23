#nullable enable
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using TheBazaar.Tooltips;
using TheBazaar.UI.Tooltips;
using TheBazaar.Utilities;
using UnityEngine;
using UnityEngine.UI;

namespace BazaarPlusPlus.GameInterop.Tooltips;

internal static class NativeCardTooltipContentRefresher
{
    internal static bool TryApply(CardTooltipController controller, CardTooltipData tooltipData)
    {
        if (controller == null || tooltipData == null || !controller.gameObject.activeInHierarchy)
            return false;

        var visibility = controller.PositioningCanvasGroup;
        var originalAlpha = visibility?.alpha ?? 0f;
        var originalInteractable = visibility?.interactable ?? false;
        var originalBlocksRaycasts = visibility?.blocksRaycasts ?? false;

        try
        {
            // ApplyPreviewTooltip rebuilds several ContentSizeFitter branches. Conceal the native
            // host for the synchronous rebuild so no intermediate geometry reaches the frame.
            if (visibility != null)
            {
                visibility.alpha = 0f;
                visibility.interactable = false;
                visibility.blocksRaycasts = false;
            }

            controller.ApplyPreviewTooltip(tooltipData);
            Canvas.ForceUpdateCanvases();
            var positioningRect = controller.PositioningRectTransform;
            if (positioningRect != null)
            {
                TempoUIUtility.ForceRebuildRecursive(positioningRect);
                LayoutRebuilder.ForceRebuildLayoutImmediate(positioningRect);
            }
            controller.KeepTooltipWithinBounds();
            // IL2CPP returns interface proxies from CurrentTooltipData, so an identity comparison
            // against the instance we passed in can never match (it reports the type name
            // "ITooltipData"). A locked controller that survived the synchronous rebuild without
            // throwing has applied the preview, so trust that instead of a reference check.
            return controller.IsLocked;
        }
        finally
        {
            if (visibility != null)
            {
                visibility.alpha = originalAlpha;
                visibility.interactable = originalInteractable;
                visibility.blocksRaycasts = originalBlocksRaycasts;
            }
        }
    }
}
