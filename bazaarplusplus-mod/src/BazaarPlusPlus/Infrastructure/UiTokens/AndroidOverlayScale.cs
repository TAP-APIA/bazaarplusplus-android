#nullable enable
namespace BazaarPlusPlus.Infrastructure.UiTokens;

// Panel chrome only: native game cards/tooltips retain their own fitting and typography.
internal static class AndroidOverlayScale
{
    internal static int Normalize(int percent) => Math.Clamp(percent, 100, 250);

    internal static int Next(int percent) =>
        Normalize(percent) switch
        {
            < 150 => 150,
            < 200 => 200,
            < 250 => 250,
            _ => 100,
        };

    internal static float Factor(int percent) => Normalize(percent) / 100f;

    internal static float CanvasFactor(
        float width,
        float height,
        int percent,
        bool matchHeight,
        bool applyPreference = true
    )
    {
        width = Positive(width);
        height = Positive(height);
        var baseline = matchHeight ? height / 1000f : MathF.Sqrt(width / 1600f * (height / 1000f));
        return baseline * (applyPreference ? Factor(percent) : 1f);
    }

    private static float Positive(float value) =>
        float.IsNaN(value) || float.IsInfinity(value) || value < 1 ? 1 : value;
}
