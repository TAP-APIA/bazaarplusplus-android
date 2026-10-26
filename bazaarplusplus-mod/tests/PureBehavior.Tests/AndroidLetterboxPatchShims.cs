#if BPP_ANDROID
using System.Reflection;

namespace UnityEngine
{
    public record struct Rect(float x, float y, float width, float height);

    public record struct Vector2Int(int x, int y);

    public sealed class Camera
    {
        public Rect rect;
    }
}

namespace TheBazaar.UI
{
    public sealed class LetterBoxManager
    {
        public static UnityEngine.Rect _currentScreenRect;

        public UnityEngine.Rect CalculateNewRect(UnityEngine.Vector2Int size) => default;
    }
}

namespace TheBazaar
{
    public sealed class CameraLetterBoxManager
    {
        public UnityEngine.Camera? _camera;
    }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch() { }

        public HarmonyPatch(Type type, string methodName) { }
    }

    public sealed class HarmonyPrepare : Attribute { }

    public sealed class HarmonyTargetMethod : Attribute { }

    public sealed class HarmonyPrefix : Attribute { }

    public sealed class HarmonyPostfix : Attribute { }

    public static class AccessTools
    {
        public static MethodInfo? Method(Type type, string name, Type[] parameters) =>
            type.GetMethod(name, parameters);
    }
}
#endif
