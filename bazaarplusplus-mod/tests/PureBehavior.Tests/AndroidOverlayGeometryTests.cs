using BazaarPlusPlus.Game.CollectionPanel;
using BazaarPlusPlus.Infrastructure.UiTokens;
using Xunit;

public sealed class AndroidOverlayGeometryTests
{
    public static TheoryData<float, float, float> Displays =>
        new()
        {
            { 1280, 720, 160 },
            { 1920, 1080, 320 },
            { 2560, 1440, 480 },
            { 2160, 1440, 320 },
            { 3096, 2064, 400 },
            { 3168, 1440, 509 },
            { 1440, 1080, 240 },
            { 800, 600, 640 },
            { 3440, 1440, 0 },
        };

    [Theory]
    [MemberData(nameof(Displays))]
    public void Panels_keep_the_whole_safe_area_so_no_dock_gutter_shows(float w, float h, float dpi)
    {
        var g = AndroidOverlayGeometry.Create(w, h, 30, 20, w - 70, h - 40, dpi);
        Assert.Equal(30, g.Content.Left);
        Assert.Equal(20, g.Content.Bottom);
        Assert.Equal(g.Safe, g.Content);
        Assert.True(
            (AndroidOverlayGeometry.DockColumnWidth + 16) * g.TouchScale <= g.Safe.Width + .01f
        );
        Assert.True(
            (AndroidOverlayGeometry.DockColumnHeight + 16) * g.TouchScale <= g.Safe.Height + .01f
        );
    }

    [Theory]
    [MemberData(nameof(Displays))]
    public void Rail_stays_inside_the_safe_area_at_every_anchor_and_size(
        float w,
        float h,
        float dpi
    )
    {
        var g = AndroidOverlayGeometry.Create(w, h, 30, 20, w - 70, h - 40, dpi);
        var anchors = new[]
        {
            (X: 1f, Y: .5f),
            (X: 0f, Y: 0f),
            (X: 0f, Y: 1f),
            (X: 1f, Y: 1f),
            (X: .5f, Y: .5f),
            (X: .5f, Y: 0f),
        };

        foreach (var anchor in anchors)
        foreach (var expanded in new[] { true, false })
        {
            var rail = AndroidOverlayGeometry.PlaceRail(
                g.Safe,
                anchor.X,
                anchor.Y,
                expanded,
                g.TouchScale
            );
            Assert.True(rail.Left >= g.Safe.Left - .01f);
            Assert.True(rail.Right <= g.Safe.Right + .01f);
            Assert.True(rail.Top <= g.Safe.Top + .01f);
            // The BPP pill is the rail's top button and must always stay fully on screen, which
            // is what keeps the dock draggable back out of a corner.
            var pillBottom = rail.Top - AndroidOverlayGeometry.ButtonHeight * g.TouchScale;
            Assert.True(pillBottom >= g.Safe.Bottom - .01f);
            if (expanded && rail.Height <= g.Safe.Height)
                Assert.True(rail.Bottom >= g.Safe.Bottom - .01f);
        }
    }

    [Theory]
    [MemberData(nameof(Displays))]
    public void Rail_hugs_both_safe_edges_when_dragged_there(float w, float h, float dpi)
    {
        var g = AndroidOverlayGeometry.Create(w, h, 30, 20, w - 70, h - 40, dpi);
        var right = AndroidOverlayGeometry.PlaceRail(g.Safe, 1f, .5f, true, g.TouchScale);
        var middle = AndroidOverlayGeometry.PlaceRail(g.Safe, .5f, .5f, true, g.TouchScale);
        var left = AndroidOverlayGeometry.PlaceRail(g.Safe, 0f, .5f, true, g.TouchScale);
        Assert.Equal(g.Safe.Right, right.Right, 3);
        Assert.Equal(g.Safe.Left, left.Left, 3);
        Assert.True(middle.Left > left.Left && middle.Right < right.Right);
    }

    [Fact]
    public void Expanded_rail_lifts_itself_above_the_bottom_edge()
    {
        var safe = new OverlayPixelRect(0, 0, 1920, 1080);
        var rail = AndroidOverlayGeometry.PlaceRail(safe, .5f, 0f, true, 2f);
        Assert.True(rail.Bottom >= -0.01f);
        Assert.True(rail.Top <= 1080.01f);
        // Collapsed keeps the pill on the anchored edge; only the hidden drawer hangs below.
        var collapsed = AndroidOverlayGeometry.PlaceRail(safe, .5f, 0f, false, 2f);
        Assert.Equal(0f, collapsed.Top - AndroidOverlayGeometry.ButtonHeight * 2, 3);
    }

    [Fact]
    public void Fixed_resolution_density_change_changes_touch_size_without_exposing_cutouts()
    {
        var a = AndroidOverlayGeometry.Create(3168, 1440, 100, 0, 3100, 1400, 240);
        var b = AndroidOverlayGeometry.Create(3168, 1440, 100, 0, 3100, 1400, 480);
        Assert.True(b.TouchScale > a.TouchScale);
        Assert.Equal(a.Safe, b.Safe);
        Assert.Equal(48 * 480f / 160, AndroidOverlayGeometry.ButtonHeight * b.TouchScale);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Missing_density_and_transient_empty_safe_area_have_finite_fallbacks(float dpi)
    {
        var g = AndroidOverlayGeometry.Create(1920, 1080, 0, 0, 0, 0, dpi);
        Assert.Equal(new OverlayPixelRect(0, 0, 1920, 1080), g.Safe);
        Assert.True(float.IsFinite(g.TouchScale) && g.TouchScale > 0);
        Assert.True(g.Content.Height > 0);
    }

    [Theory]
    [MemberData(nameof(Displays))]
    public void Inspected_card_and_actual_selector_fit_the_available_lane(
        float w,
        float h,
        float dpi
    )
    {
        var g = AndroidOverlayGeometry.Create(w, h, 30, 20, w - 70, h - 40, dpi);
        var top = h - g.Content.Top;
        var selector = g.Content.Height * .2f;
        var gap = g.TouchScale * 8;
        var card = CollectionInspectionLayout.ForDrawer(
            g.Content.Left,
            top,
            g.Content.Width * .55f,
            g.Content.Height,
            300,
            700,
            selector,
            gap
        );
        Assert.True(card.CardX >= g.Content.Left);
        Assert.True(card.CardY >= top);
        Assert.True(card.CardX + card.CardWidth <= g.Content.Left + g.Content.Width * .55f + .01f);
        Assert.True(card.CardY + card.CardHeight + gap + selector <= top + g.Content.Height + .01f);
        Assert.Equal(300f / 700f, card.CardWidth / card.CardHeight, 4);
    }

    [Fact]
    public void Inspection_scales_continuously_across_the_old_1500_pixel_boundary()
    {
        var a = CollectionInspectionLayout.ForDrawer(0, 100, 1600, 1499, 300, 700, 200, 20);
        var b = CollectionInspectionLayout.ForDrawer(0, 100, 1600, 1501, 300, 700, 200, 20);
        Assert.InRange(Math.Abs(a.CardHeight - b.CardHeight), 0, 2.1f);
        Assert.InRange(Math.Abs(a.CardY - b.CardY), 0, 2.1f);
    }

    [Fact]
    public void Inspection_same_aspect_and_scaled_inputs_produce_scaled_placement()
    {
        var a = CollectionInspectionLayout.ForDrawer(20, 80, 900, 1000, 300, 700, 150, 20);
        var b = CollectionInspectionLayout.ForDrawer(40, 160, 1800, 2000, 600, 1400, 300, 40);
        Assert.Equal(a.CardX * 2, b.CardX, 3);
        Assert.Equal(a.CardY * 2, b.CardY, 3);
        Assert.Equal(a.CardHeight * 2, b.CardHeight, 3);
    }
}
