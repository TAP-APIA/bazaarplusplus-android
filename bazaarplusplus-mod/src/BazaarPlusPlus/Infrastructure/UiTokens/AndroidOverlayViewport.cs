#nullable enable
using UnityEngine;
using UnityEngine.UI;

namespace BazaarPlusPlus.Infrastructure.UiTokens;

internal static class AndroidOverlayViewport
{
    private static Func<int>? _readScale;

    internal static int ScalePercent => AndroidOverlayScale.Normalize(_readScale?.Invoke() ?? 100);
    internal static float UiScale => AndroidOverlayScale.Factor(ScalePercent);

    internal static void InstallScale(Func<int> readScale) => _readScale = readScale;

    internal static void ResetScale() => _readScale = null;

    internal static bool ApplyCanvasScale(
        GameObject root,
        bool matchHeight,
        bool applyPreference = true
    )
    {
        var scaler = root.GetComponent<CanvasScaler>();
        var canvas = root.GetComponent<Canvas>();
        var scale = AndroidOverlayScale.CanvasFactor(
            Screen.width,
            Screen.height,
            ScalePercent,
            matchHeight,
            applyPreference
        );
        var changed =
            scaler.uiScaleMode != CanvasScaler.ScaleMode.ConstantPixelSize
            || !Mathf.Approximately(canvas.scaleFactor, scale);
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = canvas.scaleFactor = scale;
        return changed;
    }

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
