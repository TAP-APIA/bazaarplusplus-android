#nullable enable
using BazaarPlusPlus.Infrastructure;
using System;
using UnityEngine.Events;

namespace BazaarPlusPlus.GameInterop;

/// <summary>
/// Managed-delegate overloads for UnityEvent subscription.
///
/// Under IL2CPP the instance <c>AddListener</c>/<c>RemoveListener</c> overloads take
/// <c>UnityAction</c> proxies, which a lambda or method group cannot convert to. These extension
/// methods accept the managed delegate instead; the C# binder prefers the instance method when it
/// is applicable and falls back here otherwise, so call sites stay unchanged.
/// </summary>
internal static class BppUnityEventExtensions
{
    public static void AddListener(this UnityEvent unityEvent, Action action) =>
        unityEvent.AddListener(BppDelegates.UnityAction(action));

    public static void AddListener<T>(this UnityEvent<T> unityEvent, Action<T> action) =>
        unityEvent.AddListener(BppDelegates.UnityAction<T>(action));

    public static void RemoveListener(this UnityEvent unityEvent, Action action) =>
        unityEvent.RemoveListener(BppDelegates.UnityAction(action));

    public static void RemoveListener<T>(this UnityEvent<T> unityEvent, Action<T> action) =>
        unityEvent.RemoveListener(BppDelegates.UnityAction<T>(action));
}
