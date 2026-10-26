#if BPP_ANDROID
using System.Reflection;
using BazaarPlusPlus.Patches.Android;
using TheBazaar.UI;
using UnityEngine;
using Xunit;

public sealed class AndroidLetterboxPatchTests
{
    [Theory]
    [InlineData(2800, 1873)]
    [InlineData(2800, 1968)]
    [InlineData(3168, 1440)]
    public void Fullscreen_rect_is_normalized_in_both_return_and_cached_value(int width, int height)
    {
        LetterBoxManager._currentScreenRect = new Rect(0, 0, width, height);
        var patch = typeof(AndroidLetterBoxCropPatch).GetMethod(
            "Prefix",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        object?[] arguments = { new Rect() };
        Assert.False((bool)patch.Invoke(null, arguments)!);
        var result = (Rect)arguments[0]!;
        Assert.Equal(new Rect(0, 0, 1, 1), result);
        Assert.Equal(result, LetterBoxManager._currentScreenRect);
        // The native canvas multiplies the normalized crop by the physical resolution.
        Assert.Equal(height / 2160f, height * result.height / 2160f);
        Assert.Equal(width, width * result.width);
    }
}
#endif
