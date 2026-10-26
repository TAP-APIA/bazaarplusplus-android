#nullable enable
namespace BazaarPlusPlus.Infrastructure.UiTokens;

// Screen pixels, bottom-left origin. Renderers convert this rectangle exactly once.
internal readonly record struct OverlayPixelRect(
    float Left,
    float Bottom,
    float Width,
    float Height
)
{
    internal float Right => Left + Width;
    internal float Top => Bottom + Height;
}

// Where the touch dock actually rendered, in the same screen pixels as AndroidOverlayGeometry.
internal readonly record struct OverlayRailPlacement(
    float Left,
    float Bottom,
    float Width,
    float Height
)
{
    internal float Top => Bottom + Height;
    internal float Right => Left + Width;
}

internal readonly record struct AndroidOverlayGeometry(
    OverlayPixelRect Safe,
    OverlayPixelRect Content,
    float TouchScale
)
{
    // The touch dock is a vertical rail: a BPP toggle pill above the Builds/Cards/History drawer.
    // It floats above the panels, which keep the whole safe area: reserving a gutter for the rail
    // left a visible dark strip beside every panel, so the dock simply overlays instead.
    internal const float DockColumnWidth = 114;
    internal const float ButtonHeight = 48;
    internal const float DockGap = 6;
    internal const float DockPadding = 8;
    internal const int DockButtonCount = 5;
    internal const float DockColumnHeight =
        DockButtonCount * ButtonHeight + (DockButtonCount - 1) * DockGap;

    internal static AndroidOverlayGeometry Create(
        float width,
        float height,
        float safeLeft,
        float safeBottom,
        float safeRight,
        float safeTop,
        float dpi
    )
    {
        width = Positive(width, 1);
        height = Positive(height, 1);
        var left = Math.Clamp(Finite(safeLeft, 0), 0, width);
        var bottom = Math.Clamp(Finite(safeBottom, 0), 0, height);
        var right = Math.Clamp(Finite(safeRight, width), left, width);
        var top = Math.Clamp(Finite(safeTop, height), bottom, height);
        // A transient empty safeArea during activity/rotation changes is not a usable viewport.
        if (right - left < 1 || top - bottom < 1)
        {
            left = bottom = 0;
            right = width;
            top = height;
        }
        var safe = new OverlayPixelRect(left, bottom, right - left, top - bottom);
        var density = dpi is >= 120 and <= 1000 ? dpi / 160 : safe.Height / 720;
        var touchScale = Math.Max(
            .001f,
            Math.Min(
                Math.Clamp(density, .75f, 4f),
                Math.Min(
                    safe.Width * .3f / (DockColumnWidth + DockPadding * 2),
                    safe.Height * .7f / (DockColumnHeight + DockPadding * 2)
                )
            )
        );
        return new(safe, safe, touchScale);
    }

    // Places the rail so its visible part is centred on the normalized anchor, clamped so the BPP
    // pill always stays inside the safe area and the expanded drawer fits when the screen is tall
    // enough. The rail rect keeps its full column height in both states, because the drawer's
    // buttons stay parented under it while inactive.
    internal static OverlayRailPlacement PlaceRail(
        OverlayPixelRect safe,
        float anchorX,
        float anchorY,
        bool expanded,
        float scale
    )
    {
        var width = DockColumnWidth * scale;
        var height = DockColumnHeight * scale;
        var visible = expanded ? height : ButtonHeight * scale;
        var centerX = safe.Left + Math.Clamp(anchorX, 0f, 1f) * safe.Width;
        var centerY = safe.Bottom + Math.Clamp(anchorY, 0f, 1f) * safe.Height;
        var left = Math.Clamp(
            centerX - width * .5f,
            safe.Left,
            Math.Max(safe.Left, safe.Right - width)
        );
        var half = visible * .5f;
        var minCenterY = safe.Bottom + half;
        var maxCenterY = Math.Max(minCenterY, safe.Top - half);
        centerY = Math.Clamp(centerY, minCenterY, maxCenterY);
        if (expanded && height <= safe.Height)
            centerY = Math.Max(centerY, safe.Bottom + height * .5f);

        var top = centerY + half;
        return new OverlayRailPlacement(left, top - height, width, height);
    }

    private static float Finite(float value, float fallback) =>
        float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;

    private static float Positive(float value, float fallback) =>
        Math.Max(fallback, Finite(value, fallback));
}
