#nullable enable

namespace BazaarPlusPlus.Infrastructure.UiTokens;

internal static class BppOverlaySorting
{
#if BPP_ANDROID
    // On Android, the login/curtain and modal canvases sit above sortingOrder 100, so BPP's
    // overlay panels must sit higher to be visible while keeping BppTouchEntry (32000) on top.
    public const int PanelUiToolkit = 2600;
    public const int NativeCardPreview = 2601;
    public const int PanelForeground = 2602;
    public const int InspectionBackdrop = 3000;
    public const int InspectionControls = 3001;
    public const int InspectionContent = 3002;
    public const int InspectionVariantControls = 3003;
#else
    public const int PanelUiToolkit = 26;
    public const int NativeCardPreview = 27;
    public const int PanelForeground = 28;
    public const int InspectionBackdrop = 1000;
    public const int InspectionControls = 1001;
    public const int InspectionContent = 1002;
    public const int InspectionVariantControls = 1003;
#endif
}
