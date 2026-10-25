#nullable enable
using UnityEngine;

namespace BazaarPlusPlus.Infrastructure.UiTokens;

internal static class AndroidOverlayViewport
{
    internal static AndroidOverlayGeometry Read()
    {
        var safe = Screen.safeArea;
        return AndroidOverlayGeometry.Create(
            Screen.width,
            Screen.height,
            safe.xMin,
            safe.yMin,
            safe.xMax,
            safe.yMax,
            Screen.dpi
        );
    }

    internal static Rect ContentPixels
    {
        get
        {
            var content = Read().Content;
            return new Rect(content.Left, content.Bottom, content.Width, content.Height);
        }
    }
}
