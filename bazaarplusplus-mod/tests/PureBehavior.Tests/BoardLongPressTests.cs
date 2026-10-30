using BazaarPlusPlus.GameInterop.MonsterBoardPreview;
using Xunit;

public sealed class BoardLongPressTests
{
    private const float Slop = 16f;

    [Fact]
    public void Held_still_fires_once_at_the_threshold_and_marks_the_release()
    {
        var press = new BoardLongPress(Slop);
        Assert.False(press.Sample(true, true, 100, 200, 0f));
        Assert.False(press.Sample(false, true, 104, 203, BoardLongPress.HoldSeconds - .01f));
        Assert.True(press.Sample(false, true, 104, 203, BoardLongPress.HoldSeconds));
        Assert.False(press.Sample(false, true, 104, 203, 2f));
        Assert.False(press.Sample(false, false, 104, 203, 2.1f));
        Assert.True(press.Fired);
        Assert.Equal((100f, 200f), (press.StartX, press.StartY));
    }

    [Fact]
    public void A_short_tap_never_fires()
    {
        var press = new BoardLongPress(Slop);
        Assert.False(press.Sample(true, true, 10, 10, 0f));
        Assert.False(press.Sample(false, false, 10, 10, .1f));
        Assert.False(press.Sample(false, false, 10, 10, 1f));
        Assert.False(press.Fired);
    }

    [Fact]
    public void Both_edges_in_one_frame_are_a_tap()
    {
        var press = new BoardLongPress(Slop);
        Assert.False(press.Sample(true, false, 10, 10, 0f));
        Assert.False(press.Sample(false, false, 10, 10, 1f));
        Assert.False(press.Fired);
    }

    [Fact]
    public void Moving_past_the_slop_cancels_even_if_the_finger_returns()
    {
        var press = new BoardLongPress(Slop);
        press.Sample(true, true, 0, 0, 0f);
        Assert.False(press.Sample(false, true, 0, Slop + 1, .2f));
        Assert.False(press.Sample(false, true, 0, 0, 1f));
        Assert.False(press.Fired);
    }

    [Fact]
    public void A_new_press_clears_the_previous_fire()
    {
        var press = new BoardLongPress(Slop);
        press.Sample(true, true, 0, 0, 0f);
        press.Sample(false, true, 0, 0, 1f);
        Assert.True(press.Fired);
        press.Sample(true, true, 50, 50, 2f);
        Assert.False(press.Fired);
        Assert.True(press.Sample(false, true, 50, 50, 2f + BoardLongPress.HoldSeconds));
    }

    [Fact]
    public void Cancel_drops_the_press_and_its_fire()
    {
        var press = new BoardLongPress(Slop);
        press.Sample(true, true, 0, 0, 0f);
        press.Sample(false, true, 0, 0, 1f);
        press.Cancel();
        Assert.False(press.Fired);
        Assert.False(press.Sample(false, true, 0, 0, 3f));
    }
}
