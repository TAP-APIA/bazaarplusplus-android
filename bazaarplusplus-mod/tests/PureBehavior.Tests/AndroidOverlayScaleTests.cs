using BazaarPlusPlus.Game.LiveBuildPanel.Ui;
using BazaarPlusPlus.GameInterop.MonsterBoardPreview;
using BazaarPlusPlus.Infrastructure.UiTokens;
using Xunit;

public sealed class AndroidOverlayScaleTests
{
    private static readonly int[] FontPercents = [100, 150, 200, 250];

    [Fact]
    public void Panel_layers_preserve_content_order_without_hiding_native_system_popups()
    {
#if BPP_ANDROID
        // Device runtime fixture: LetterBoxOverlays/Canvas = 5000, native popups = 9999.
        Assert.True(BppOverlaySorting.PanelUiToolkit > 5000);
        Assert.True(BppOverlaySorting.InspectionVariantControls < 9999);
#endif
        Assert.True(BppOverlaySorting.NativeCardPreview > BppOverlaySorting.PanelUiToolkit);
        Assert.True(BppOverlaySorting.PanelForeground > BppOverlaySorting.NativeCardPreview);
        Assert.True(BppOverlaySorting.InspectionBackdrop > BppOverlaySorting.PanelForeground);
        Assert.True(BppOverlaySorting.InspectionDescription > BppOverlaySorting.InspectionContent);
        Assert.True(
            BppOverlaySorting.InspectionVariantControls > BppOverlaySorting.InspectionDescription
        );
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(0, 100)]
    [InlineData(100, 100)]
    [InlineData(171, 171)]
    [InlineData(150, 150)]
    [InlineData(250, 250)]
    [InlineData(int.MaxValue, 250)]
    public void Untrusted_config_has_a_bounded_scale(int stored, int expected) =>
        Assert.Equal(expected, AndroidOverlayScale.Normalize(stored));

    [Fact]
    public void Touch_choice_cycles_back_to_standard_and_accepts_custom_values()
    {
        var current = 100;
        foreach (var expected in new[] { 150, 200, 250, 100 })
        {
            current = AndroidOverlayScale.Next(current);
            Assert.Equal(expected, current);
        }
        Assert.Equal(200, AndroidOverlayScale.Next(175));
        Assert.Equal(100, AndroidOverlayScale.Next(int.MaxValue));
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(3168, 1440)]
    [InlineData(2800, 1968)]
    public void Both_renderers_keep_their_baseline_and_enlarge_continuously(float w, float h)
    {
        foreach (var matchHeight in new[] { false, true })
        {
            var baseline = matchHeight ? h / 1000 : MathF.Sqrt(w / 1600 * h / 1000);
            foreach (var percent in new[] { 100, 150, 200, 250 })
            {
                var scale = AndroidOverlayScale.CanvasFactor(w, h, percent, matchHeight);
                Assert.Equal(baseline * percent / 100, scale, 4);
                // Insets round-trip through canvas units exactly once, at every preference.
                Assert.Equal(100, 100 / scale * scale, 3);
            }
        }
    }

    [Theory]
    [InlineData(float.NaN, 0)]
    [InlineData(float.PositiveInfinity, -1)]
    public void Transient_display_measurements_remain_finite(float w, float h) =>
        Assert.True(float.IsFinite(AndroidOverlayScale.CanvasFactor(w, h, 250, false)));

    [Theory]
    [InlineData(3168, 1440, 0)]
    [InlineData(2800, 1968, 95)]
    [InlineData(3096, 2064, 0)]
    public void Readable_carpets_keep_the_same_scale_at_100_150_200_250_percent(
        float w,
        float h,
        float topInset
    )
    {
        float? expectedScale = null;
        var expectedWidth = 0f;
        foreach (var percent in FontPercents)
        {
            var (panel, scale) = ReadablePanel(w, h, topInset, percent);
            Assert.True(panel.BoardHeight >= LiveBuildPanelLayout.ReadableBoardMinHeight - .001f);
            expectedWidth = expectedWidth == 0 ? panel.BoardWidth : expectedWidth;
            Assert.Equal(expectedWidth, panel.BoardWidth, 3);
            var carpet = CarpetScale(panel, scale);
            expectedScale ??= carpet;
            Assert.Equal(expectedScale.Value, carpet, 6);
        }
    }

    [Theory]
    [InlineData(3168, 1440, 0)]
    [InlineData(2800, 1968, 95)]
    [InlineData(3096, 2064, 0)]
    public void Readable_carpets_are_at_least_as_large_as_the_previous_125_percent_design(
        float w,
        float h,
        float topInset
    )
    {
        var oldScale = AndroidOverlayScale.CanvasFactor(w, h, 125, true);
        var oldPanel = new LiveBuildPanelLayout(w / oldScale, (h - topInset) / oldScale);
        var oldCarpet = NativeMonsterBoardLayout.Calculate(
            oldPanel.BoardWidth * oldScale,
            oldPanel.BoardHeight * oldScale,
            2600,
            550,
            0,
            0,
            itemsOnly: true,
            itemFooterHeight: LiveBuildPanelLayout.BoardFooterHeight * oldScale,
            itemTopInset: LiveBuildPanelLayout.BoardTopInset * oldScale
        );
        foreach (var percent in FontPercents)
        {
            var (panel, scale) = ReadablePanel(w, h, topInset, percent);
            Assert.True(panel.BoardHeight >= oldPanel.BoardHeight - .001f);
            Assert.True(CarpetScale(panel, scale) >= oldCarpet.BoardScale - .000001f);
        }
    }

    [Theory]
    [InlineData(3168, 1440, 0)]
    [InlineData(2800, 1968, 95)]
    [InlineData(3096, 2064, 0)]
    public void Readable_rows_never_overlap_at_any_font_scale(float w, float h, float topInset)
    {
        foreach (var percent in FontPercents)
        {
            var (panel, _) = ReadablePanel(w, h, topInset, percent);
            for (var row = 0; row < 4; row++)
            {
                Assert.True(
                    panel.BoardTop(row) >= panel.TitleTop(row) + panel.HeaderRowHeight - .001f
                );
                if (row > 0)
                    Assert.True(
                        panel.TitleTop(row) >= panel.BoardTop(row - 1) + panel.BoardHeight - .001f
                    );
            }
            Assert.True(panel.BoardTop(3) + panel.BoardHeight <= panel.BoardContentHeight + .001f);
        }
    }

    [Theory]
    [InlineData(3168, 1440, 0)]
    [InlineData(2800, 1968, 95)]
    [InlineData(3096, 2064, 0)]
    public void Readable_content_scrolls_instead_of_shrinking_boards_at_large_font_scales(
        float w,
        float h,
        float topInset
    )
    {
        foreach (var percent in new[] { 200, 250 })
        {
            var (panel, scale) = ReadablePanel(w, h, topInset, percent);
            Assert.True(panel.BoardScrolls);
            Assert.True(panel.BoardMaxScroll > 1);
            Assert.Equal(LiveBuildPanelLayout.ReadableBoardMinHeight, panel.BoardHeight, 3);
            Assert.True(panel.BoardContentHeight > panel.BoardViewportHeight);
            // The last row is fully reachable: scrolling to the end puts its bottom at the
            // viewport bottom, not past it.
            Assert.True(
                panel.BoardContentHeight - panel.BoardMaxScroll <= panel.BoardViewportHeight + .001f
            );
            // The font preference changes chrome height, never the carpet scale.
            Assert.True(CarpetScale(panel, scale) > 0);
        }
    }

    [Fact]
    public void Readable_tall_viewport_keeps_all_four_rows_without_scrolling()
    {
        var panel = new LiveBuildPanelLayout(1600, 2000, ReadableChrome: true);
        Assert.False(panel.BoardScrolls);
        Assert.Equal(0, panel.BoardMaxScroll, 3);
        Assert.True(panel.BoardHeight > LiveBuildPanelLayout.ReadableBoardMinHeight);
        Assert.True(panel.BoardTop(3) + panel.BoardHeight <= panel.BoardViewportHeight + .001f);
    }

    private static (LiveBuildPanelLayout Panel, float Scale) ReadablePanel(
        float w,
        float h,
        float topInset,
        int percent
    )
    {
        var scale = AndroidOverlayScale.CanvasFactor(w, h, percent, true, applyPreference: false);
        return (
            new LiveBuildPanelLayout(
                w / scale,
                (h - topInset) / scale,
                ReadableChrome: true,
                ChromeScale: AndroidOverlayScale.Factor(percent)
            ),
            scale
        );
    }

    private static float CarpetScale(LiveBuildPanelLayout panel, float scale) =>
        NativeMonsterBoardLayout
            .Calculate(
                panel.BoardWidth * scale,
                panel.BoardHeight * scale,
                2600,
                550,
                0,
                0,
                itemsOnly: true,
                itemFooterHeight: LiveBuildPanelLayout.ReadableBoardFooterHeight * scale,
                itemTopInset: LiveBuildPanelLayout.BoardTopInset * scale
            )
            .BoardScale;
}
