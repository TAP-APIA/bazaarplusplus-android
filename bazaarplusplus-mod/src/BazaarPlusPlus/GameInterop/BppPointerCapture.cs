#nullable enable
using UnityEngine.UIElements;

namespace BazaarPlusPlus.GameInterop;

/// <summary>
/// Cross-platform pointer-capture helpers.
///
/// UIElements exposes <c>CapturePointer</c>/<c>ReleasePointer</c>/<c>HasPointerCapture</c> as
/// extension methods on <c>IEventHandler</c>. Under IL2CPP the <c>VisualElement</c> proxy does not
/// declare that interface, so the extension methods disappear; the static
/// <see cref="PointerCaptureHelper"/> entry points still work once the element is cast to the
/// projected <c>IEventHandler</c>.
/// </summary>
internal static class BppPointerCapture
{
    public static bool HasPointerCapture(VisualElement element, int pointerId)
    {
#if BPP_ANDROID
        return PointerCaptureHelper.HasPointerCapture(
            element.TryCast<UnityEngine.UIElements.IEventHandler>()!,
            pointerId
        );
#else
        return element.HasPointerCapture(pointerId);
#endif
    }

    public static void CapturePointer(VisualElement element, int pointerId)
    {
#if BPP_ANDROID
        PointerCaptureHelper.CapturePointer(
            element.TryCast<UnityEngine.UIElements.IEventHandler>()!,
            pointerId
        );
#else
        element.CapturePointer(pointerId);
#endif
    }

    public static void ReleasePointer(VisualElement element, int pointerId)
    {
#if BPP_ANDROID
        PointerCaptureHelper.ReleasePointer(
            element.TryCast<UnityEngine.UIElements.IEventHandler>()!,
            pointerId
        );
#else
        element.ReleasePointer(pointerId);
#endif
    }
}
