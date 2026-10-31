using System.Reflection;
using HarmonyLib;
using Il2CppTheBazaar;
using Il2CppTheBazaar.UI;
using UnityEngine;

namespace BazaarPlusPlus.BepInExBridge;

/// <summary>
/// Removes the game's own 16:10 letterbox so the full viewport is used, as this fork's plugin
/// does (bazaarplusplus-mod Patches/Android/AndroidLetterboxPatches). Upstream's payload has no
/// counterpart, and on a 3:2 tablet the game otherwise draws bars above and below.
///
/// <c>LetterBoxManager.CalculateNewRect</c> sizes the black panels and
/// <c>CameraLetterBoxManager.OnScreenSizeChanged</c> narrows the main camera. Both are
/// presentation-only, so forcing their result to the screen rect does not touch gameplay state.
/// </summary>
internal static class LetterBoxPatches
{
    public static void Apply(Harmony harmony)
    {
        harmony.PatchAll(typeof(LetterBoxCropPatch));
        harmony.PatchAll(typeof(CameraLetterBoxPatch));
    }

    [HarmonyPatch]
    private static class LetterBoxCropPatch
    {
        [HarmonyPrepare]
        private static bool Prepare() => Target() != null;

        [HarmonyTargetMethod]
        private static MethodBase? Target() =>
            AccessTools.Method(typeof(LetterBoxManager), "CalculateNewRect", new[] { typeof(Vector2Int) });

        [HarmonyPrefix]
        private static bool Prefix(ref Rect __result)
        {
            // CurrentScreenRect is normalized; BazaarCanvasScaler multiplies it by screen pixels.
            // CalculateNewRect also caches its result, so replacing only the return value is unsafe.
            __result = new Rect(0f, 0f, 1f, 1f);
            LetterBoxManager._currentScreenRect = __result;
            return false;
        }
    }

    [HarmonyPatch(typeof(CameraLetterBoxManager), "OnScreenSizeChanged")]
    private static class CameraLetterBoxPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CameraLetterBoxManager __instance)
        {
            var camera = __instance._camera;
            if (camera != null)
                camera.rect = new Rect(0f, 0f, 1f, 1f);
        }
    }
}
