#nullable enable
using System;
using UnityEngine.UIElements;
#if BPP_ANDROID
using Il2CppInterop.Runtime;
#endif

namespace BazaarPlusPlus.Infrastructure;

/// <summary>
/// Bridges managed delegates to whatever delegate type the current runtime expects.
///
/// On the Mono build the game's events take ordinary delegates, so the helpers return the input
/// unchanged. On IL2CPP the same events are projected as <c>Il2CppSystem.Action</c>,
/// <c>Il2CppSystem.Predicate&lt;T&gt;</c> and <c>UnityEngine.UIElements.EventCallback&lt;T&gt;</c>
/// proxies, which only expose the native <c>(object, IntPtr)</c> constructor; those need
/// <see cref="DelegateSupport.ConvertDelegate{TDelegate}"/>. Call sites therefore pass a method
/// group to these helpers and stay platform-agnostic.
/// </summary>
internal static class BppDelegates
{
#if BPP_ANDROID
    public static Il2CppSystem.Action Action(Action action) =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(action);

    public static Il2CppSystem.Action<T> Action<T>(Action<T> action) =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Action<T>>(action);

    public static Il2CppSystem.Predicate<T> Predicate<T>(Predicate<T> predicate) =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Predicate<T>>(predicate);

    public static EventCallback<T> EventCallback<T>(Action<T> callback) =>
        DelegateSupport.ConvertDelegate<EventCallback<T>>(callback);

    public static UnityEngine.Events.UnityAction UnityAction(Action action) =>
        DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(action);

    public static UnityEngine.Events.UnityAction<T> UnityAction<T>(Action<T> action) =>
        DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<T>>(action);

    public static TMPro.TMP_InputField.OnValidateInput OnValidateInput(
        Func<string, int, char, char> callback
    ) => DelegateSupport.ConvertDelegate<TMPro.TMP_InputField.OnValidateInput>(callback);
#else
    public static Action Action(Action action) => action;

    public static Action<T> Action<T>(Action<T> action) => action;

    public static Predicate<T> Predicate<T>(Predicate<T> predicate) => predicate;

    public static EventCallback<T> EventCallback<T>(Action<T> callback) => new(callback);

    public static UnityEngine.Events.UnityAction UnityAction(Action action) => new(action);

    public static UnityEngine.Events.UnityAction<T> UnityAction<T>(Action<T> action) =>
        new(action);

    public static TMPro.TMP_InputField.OnValidateInput OnValidateInput(
        Func<string, int, char, char> callback
    ) => new(callback);
#endif
}
