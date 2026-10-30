#nullable enable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace BazaarPlusPlus.GameInterop;

/// <summary>
/// Drives managed <see cref="IEnumerator"/> coroutines, main-thread task continuations, and
/// IL2CPP task polling on the Unity main thread.
///
/// On IL2CPP + CoreCLR, <see cref="SynchronizationContext.Current"/> is <c>null</c> on the Unity
/// main thread by default, so any <c>await</c> inside an <c>async</c> method schedules its
/// continuation onto a <c>.NET TP Worker</c> thread, where Unity graphics/instantiate calls abort
/// with <c>Graphics device is null</c>. Installing <see cref="MainThreadSyncContext"/> keeps all
/// <c>await</c> continuations on <c>UnityMain</c>.
/// </summary>
internal sealed class BppCoroutines : MonoBehaviour
{
    private static BppCoroutines? _runner;
    private static readonly MainThreadSyncContext SyncContext = new();
    private static readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> MainThreadQueue = new();
    private static readonly List<Func<bool>> Pollers = new();
    private static int _mainThreadId;

    private readonly List<IEnumerator> _active = new();

#if BPP_ANDROID
    static BppCoroutines()
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<BppCoroutines>();
    }
#endif

    /// <summary>Managed id of the Unity main thread; 0 until <see cref="InitializeMainThread"/> runs.</summary>
    internal static int MainThreadId => _mainThreadId;

    internal static void InitializeMainThread()
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
        SynchronizationContext.SetSynchronizationContext(SyncContext);
        EnsureRunner();
    }

    internal static void PollOnMainThread(Func<bool> pollUntilTrue)
    {
        EnsureRunner();
        lock (Pollers)
        {
            Pollers.Add(pollUntilTrue);
        }
    }

#if BPP_ANDROID
    internal static Coroutine? Start(object host, IEnumerator routine)
    {
        _ = host;
        EnsureRunner();
        _runner!._active.Add(routine);
        return null;
    }
#endif

    internal static Coroutine? Start(MonoBehaviour host, IEnumerator routine)
    {
#if BPP_ANDROID
        EnsureRunner();
        _runner!._active.Add(routine);
        return null;
#else
        return host.StartCoroutine(routine);
#endif
    }

#if BPP_ANDROID
    internal static void Stop(object host, Coroutine? handle)
    {
        _ = host;
        _ = handle;
    }
#endif

    internal static void Stop(MonoBehaviour host, Coroutine? handle)
    {
#if !BPP_ANDROID
        if (handle != null)
            host.StopCoroutine(handle);
#endif
    }

    private static void EnsureRunner()
    {
        if (_runner != null)
            return;
        var go = BppGameObjectFactory.Create("BppCoroutines");
        UnityEngine.Object.DontDestroyOnLoad(go);
        _runner = go.AddComponent<BppCoroutines>();
    }

    private void Update()
    {
        if (SynchronizationContext.Current != SyncContext)
            SynchronizationContext.SetSynchronizationContext(SyncContext);

        // 1. Advance IL2CPP task pollers on UnityMain before draining continuations.
        lock (Pollers)
        {
            for (var i = Pollers.Count - 1; i >= 0; i--)
            {
                bool done;
                try
                {
                    done = Pollers[i]();
                }
                catch
                {
                    done = true;
                }
                if (done)
                    Pollers.RemoveAt(i);
            }
        }

        // 2. Drain SynchronizationContext continuations on UnityMain.
        while (MainThreadQueue.TryDequeue(out var work))
        {
            try
            {
                work.Callback(work.State);
            }
            catch
            {
                // Swallowed to keep the main-thread pump alive.
            }
        }

        // 3. Advance managed IEnumerator coroutines.
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var routine = _active[i];
            bool moved;
            try
            {
                moved = routine.MoveNext();
            }
            catch
            {
                moved = false;
            }

            if (!moved)
            {
                _active.RemoveAt(i);
                continue;
            }

            if (routine.Current is IEnumerator nested)
                _active.Add(nested);
        }
    }

    private sealed class MainThreadSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            MainThreadQueue.Enqueue((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Environment.CurrentManagedThreadId == _mainThreadId)
                d(state);
            else
                MainThreadQueue.Enqueue((d, state));
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
