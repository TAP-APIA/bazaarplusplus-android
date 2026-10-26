using BazaarPlusPlus.Infrastructure.UiTokens;
using Xunit;

public sealed class AndroidOverlayScaleLadderTests
{
    [Theory]
    [InlineData(-1, 100)]
    [InlineData(0, 100)]
    [InlineData(100, 100)]
    [InlineData(175, 175)]
    [InlineData(250, 250)]
    [InlineData(int.MaxValue, 250)]
    public void Stored_scale_is_bounded_to_the_advertised_range(int stored, int expected) =>
        Assert.Equal(expected, AndroidOverlayScale.Normalize(stored));

    [Fact]
    public void Touch_choice_cycles_through_100_150_200_250_and_back()
    {
        var current = 100;
        foreach (var expected in new[] { 150, 200, 250, 100 })
            Assert.Equal(expected, current = AndroidOverlayScale.Next(current));
        Assert.Equal(200, AndroidOverlayScale.Next(175));
        Assert.Equal(100, AndroidOverlayScale.Next(int.MaxValue));
    }

    [Theory]
    [InlineData(100, 1f)]
    [InlineData(150, 1.5f)]
    [InlineData(200, 2f)]
    [InlineData(250, 2.5f)]
    public void Preference_is_a_direct_percent_multiplier(int percent, float expected) =>
        Assert.Equal(expected, AndroidOverlayScale.Factor(percent));
}
