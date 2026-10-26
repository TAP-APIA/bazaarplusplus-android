#nullable enable

namespace BazaarPlusPlus.Infrastructure.UiTokens;

internal static class BppOverlaySorting
{
#if BPP_ANDROID
    // Android's LetterBoxOverlays canvas sorts at 5000 even when Screen.safeArea is full-screen.
    // Keep BPP's modal walls/content above that frame so it cannot cut off close buttons/cards.
    // Native system popups (9999) and the touch dock (32000) still stay above the panels.
    public const int PanelUiToolkit = 6000;
    public const int NativeCardPreview = 6001;
    public const int PanelForeground = 6002;
    public const int InspectionBackdrop = 7000;
    public const int InspectionControls = 7001;
    public const int InspectionContent = 7002;

    // The inspection description is a descendant of the mobile tooltip's Scroll View, so it is
    // promoted onto its own canvas above every tray decoration. Variant chips stay above it.
    public const int InspectionDescription = 7003;
    public const int InspectionVariantControls = 7004;
#else
    public const int PanelUiToolkit = 26;
    public const int NativeCardPreview = 27;
    public const int PanelForeground = 28;
    public const int InspectionBackdrop = 1000;
    public const int InspectionControls = 1001;
    public const int InspectionContent = 1002;
    public const int InspectionDescription = 1003;
    public const int InspectionVariantControls = 1004;
#endif
}
