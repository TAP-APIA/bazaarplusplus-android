#if BPP_ANDROID
#nullable enable
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using TheBazaar.UI;

namespace BazaarPlusPlus.Patches.Android;

/// <summary>
/// Removes the game's own 16:10 letterbox on Android so the full viewport is used.
///
/// `TheBazaar.UI.LetterBoxManager.CalculateNewRect` sizes the two black side panels and
/// `TheBazaar.CameraLetterBoxManager.OnScreenSizeChanged` narrows the main camera. Both are
/// presentation-only, so forcing their result to the screen rect does not touch gameplay state.
/// These patches stop the game from resetting the crop on resolution/safe-area events without
/// mutating unrelated render-texture or auxiliary cameras.
/// </summary>
[HarmonyPatch]
internal static class AndroidLetterBoxCropPatch
{
    [HarmonyPrepare]
    private static bool Prepare() => Target() != null;

    [HarmonyTargetMethod]
    private static MethodBase? Target() =>
        AccessTools.Method(
            typeof(LetterBoxManager),
            "CalculateNewRect",
            new[] { typeof(Vector2Int) }
        );

    [HarmonyPrefix]
    private static bool Prefix(ref Rect __result)
    {
        // CurrentScreenRect is normalized. BazaarCanvasScaler multiplies it by screen pixels;
        // pixel dimensions here multiply the native canvas scale by the screen height again.
        // CalculateNewRect also caches its result, so replacing only the return value is unsafe.
        __result = new Rect(0f, 0f, 1f, 1f);
        LetterBoxManager._currentScreenRect = __result;
        return false;
    }
}

[HarmonyPatch(typeof(TheBazaar.CameraLetterBoxManager), "OnScreenSizeChanged")]
internal static class AndroidCameraLetterBoxPatch
{
    [HarmonyPostfix]
    private static void Postfix(TheBazaar.CameraLetterBoxManager __instance)
    {
        var camera = __instance._camera;
        if (camera == null)
            return;
        camera.rect = new Rect(0f, 0f, 1f, 1f);
    }
}
#endif
