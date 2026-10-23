#nullable enable
using HarmonyLib;
using TheBazaar.UI;
using UnityEngine;

namespace BazaarPlusPlus.GameInterop.CardPreview;

internal static class NativeCardPreviewLifecycle
{
    // The callback lives here rather than on the marker: a MonoBehaviour method that takes a managed
    // delegate gets an unsupported signature in the IL2CPP projection, and the injector aborts while
    // registering it. Keying by instance id keeps the component's surface projection-safe.
    private static readonly Dictionary<int, Action> Callbacks = new();

    internal static void Bind(GameObject root, Action onDestroyed)
    {
        if (root == null)
            throw new ArgumentNullException(nameof(root));
        if (onDestroyed == null)
            throw new ArgumentNullException(nameof(onDestroyed));

        var marker = root.GetComponent<NativeCardPreviewLifecycleMarker>();
        if (marker == null)
            marker = root.AddComponent<NativeCardPreviewLifecycleMarker>();

        Callbacks[root.GetInstanceID()] = onDestroyed;
    }

    internal static void Disarm(GameObject root)
    {
        if (root == null)
            return;
        Callbacks.Remove(root.GetInstanceID());
    }

    internal static void NotifyDestroyed(GameObject root)
    {
        if (root == null)
            return;

        if (!Callbacks.Remove(root.GetInstanceID(), out var callback))
            return;

        callback?.Invoke();
    }
}

internal sealed class NativeCardPreviewLifecycleMarker : MonoBehaviour { }

[HarmonyPatch(typeof(CardPreviewBase), "OnDestroy")]
internal static class NativeCardPreviewLifecyclePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(CardPreviewBase __instance) =>
        NativeCardPreviewLifecycle.NotifyDestroyed(__instance.gameObject);
}
