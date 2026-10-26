#nullable enable
namespace BazaarPlusPlus.Game.LiveBuildPanel.Ui;

// Layout units are the owning canvas's units, not physical screen pixels.
internal readonly record struct LiveBuildPanelLayout(
    float Width,
    float Height,
    bool ReadableChrome = false,
    float ChromeScale = 1
)
{
    internal const float HeaderHeight = 24;
    internal const float RowTitleHeight = 28;
    internal const float RowGap = 12;

    // Two stacked lines per metric cell: an 11px label over a 16px value. Every unit
    // here comes straight out of BoardHeight, and the carpet is height-constrained.
    internal const float MetricsHeight = 34;
    internal const int MetricCells = 4;
    internal const float FooterHeight = 28;
    internal const float SidebarWidth = 290;
    internal const float SidebarGap = 28;
    internal const float BadgeHeight = 18;
    internal const float BoardFooterHeight = BadgeHeight + 5;
    internal const float BoardTopInset = 3;

    // Readable chrome is the Android layout. Its baseline sizes are multiplied by ChromeScale
    // (the font preference), while the reserved sidebar stays at the largest scale the overview
    // supports. The sidebar is what the carpet rail competes with for width, so keeping it fixed
    // is what makes the carpet the same physical size across the font-size ladder.
    internal const float ReadableBadgeHeight = 26;
    internal const float ReadableBoardFooterHeight = ReadableBadgeHeight + 5;
    internal const float ReadableSidebarScaleMax = 2;
    internal const float ReadableFirstHeaderHeight = 12;
    internal const float ReadableHeaderRowHeight = 40;
    internal const float ReadableMetricRowHeight = 48;
    internal const float ReadableSectionGap = 6;
    private const float ReadableFooterBaseHeight = 28;

    // Four rows no longer have to share one viewport: each keeps this height and the
    // board column scrolls. The carpet is 2600:550, so a taller row only pays off while
    // the rail is wide enough to be height-constrained.
    internal const float ReadableBoardMinHeight = 320;

    // A touch that moves this far is a scroll, not a candidate tap. Screen pixels.
    internal const float BoardDragThresholdPixels = 16;

    private float Scale =>
        ReadableChrome && float.IsFinite(ChromeScale) ? Math.Clamp(ChromeScale, 1f, 4f) : 1f;

    internal float HeaderRowHeight =>
        ReadableChrome ? ReadableHeaderRowHeight * Scale : RowTitleHeight;
    internal float MetricRowHeight =>
        ReadableChrome ? ReadableMetricRowHeight * Scale : MetricsHeight;
    internal float FirstHeaderHeight =>
        ReadableChrome ? ReadableFirstHeaderHeight * Scale : HeaderHeight;
    internal float SectionGap => ReadableChrome ? ReadableSectionGap * Scale : RowGap;

    // The footer carries the selection hint, so it grows with the text it holds. The
    // viewport ends above it; the board column scrolls behind the mask.
    internal float ChromeFooterHeight =>
        ReadableChrome ? ReadableFooterBaseHeight * Scale : FooterHeight;
    internal float ReservedSidebarWidth =>
        ReadableChrome ? SidebarWidth * ReadableSidebarScaleMax : SidebarWidth;
    internal float Padding => Math.Min(28, Width * .025f);
    internal float BoardLeft => Padding;
    internal float BoardWidth =>
        Math.Max(1, Width - Padding * 2 - ReservedSidebarWidth - SidebarGap);

    internal float SidebarLeft(float carpetLeft, float carpetWidth) =>
        carpetLeft + carpetWidth + SidebarGap;

    // The fit height is computed from the unscaled baseline: ChromeScale grows the row
    // chrome and the scroll content, never the board itself.
    private float FitBoardHeight =>
        Math.Max(
            1,
            (
                Height
                - (ReadableChrome ? ReadableFirstHeaderHeight : HeaderHeight)
                - FooterHeight
                - (ReadableChrome ? ReadableHeaderRowHeight : RowTitleHeight) * 4
                - (ReadableChrome ? ReadableSectionGap : RowGap) * 3
                - (ReadableChrome ? ReadableMetricRowHeight : MetricsHeight)
            ) / 4
        );

    internal float BoardHeight =>
        ReadableChrome ? Math.Max(ReadableBoardMinHeight, FitBoardHeight) : FitBoardHeight;

    internal float TitleTop(int row) =>
        FirstHeaderHeight
        + row * (HeaderRowHeight + BoardHeight + SectionGap)
        + (row > 0 ? MetricRowHeight : 0);

    internal float BoardTop(int row) => TitleTop(row) + HeaderRowHeight;

    internal float MetricsTop => BoardTop(0) + BoardHeight;
    internal float FooterTop => Height - ChromeFooterHeight;

    // Board column viewport: everything above the footer. Content is the four row blocks,
    // laid out top-down; BoardMaxScroll is what the ScrollRect is allowed to reveal.
    internal float BoardViewportTop => 0;
    internal float BoardViewportHeight => Math.Max(1, FooterTop - BoardViewportTop);
    internal float BoardContentHeight => BoardTop(3) + BoardHeight;
    internal float BoardMaxScroll => Math.Max(0, BoardContentHeight - BoardViewportHeight);
    internal bool BoardScrolls => ReadableChrome && BoardMaxScroll > .5f;

    // Keep the navigation and full supporter list disjoint on enlarged phone UI. At the
    // standard 1000-unit height these retain the desktop placement.
    internal float CorpusTop => Math.Clamp(Height - 650, 88, 156);
    internal float NavigationTop => Math.Min(492, Math.Max(228, Height - 508));
    internal float SupportersTop => Math.Max(NavigationTop + 116, Height - 286);
    internal float SupportersViewportHeight => Math.Clamp(Height - SupportersTop - 28, 48, 240);
}
