#nullable enable
using System;
using UnityEngine;
#if BPP_ANDROID
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
#endif

namespace BazaarPlusPlus.GameInterop;

/// <summary>
/// Cross-platform bridge for the two Unity APIs whose IL2CPP projection swaps
/// <c>System.Type</c> for <c>Il2CppSystem.Type</c>: the <c>GameObject(string, params Type[])</c>
/// constructor and <c>GetComponent(Type)</c>. Call sites keep plain <c>typeof</c>.
/// </summary>
internal static class BppGameObjectFactory
{
    public static GameObject Create(string name, params Type[] components)
    {
#if BPP_ANDROID
        // The overload that takes a type array resolves every entry through Il2CppType; with an empty
        // array that resolution still runs and the interop hook dereferences a null class, aborting
        // the process. The plain constructor is the only safe shape when no component is requested.
        if (components.Length == 0)
            return new GameObject(name);

        var types = new Il2CppReferenceArray<Il2CppSystem.Type>(components.Length);
        for (var i = 0; i < components.Length; i++)
            types[i] = Il2CppType.From(components[i]);
        return new GameObject(name, types);
#else
        return new GameObject(name, components);
#endif
    }

    public static Component? GetComponent(GameObject target, Type componentType)
    {
#if BPP_ANDROID
        var comp = target.GetComponent(Il2CppType.From(componentType));
        if (comp == null)
            return null;
        if (componentType.IsInstanceOfType(comp))
            return comp;
        if (componentType == typeof(TheBazaar.UI.CardPreviewBase))
            return comp.TryCast<TheBazaar.UI.CardPreviewBase>();
        var tryCast = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
            .GetMethod("TryCast")
            ?.MakeGenericMethod(componentType);
        return tryCast != null ? tryCast.Invoke(comp, null) as Component : comp;
#else
        return target.GetComponent(componentType);
#endif
    }
}
