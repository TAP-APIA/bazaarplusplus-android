#nullable enable
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using BazaarPlusPlus.GameInterop;
using BazaarPlusPlus.Infrastructure.Logging;

namespace BazaarPlusPlus.Infrastructure;

/// <summary>
/// Temporary probe: reports BPP code that reaches the IL2CPP runtime off the Unity main thread.
///
/// A 2026-10-29 Android crash died in <c>il2cpp_unity_liveness_calculation_*</c> on the
/// <c>AssetGarbageCollector</c> thread after returning to the main menu: a managed field pointed at
/// a block the IL2CPP GC had already freed. CoreCLR thread-pool threads are not registered with
/// the IL2CPP GC, so an IL2CPP object reachable only from such a thread's stack can be collected
/// while still in use. Each call site reports once, with its stack, so one log line names the
/// offender. Delete this probe together with the change that acts on its measurement.
/// </summary>
internal static class Il2CppThreadProbe
{
    private static readonly ConcurrentDictionary<string, byte> ReportedSites = new();

    /// <summary>Call on the main thread right after <see cref="BppCoroutines.InitializeMainThread"/>.</summary>
    [Conditional("BPP_ANDROID")]
    internal static void Arm()
    {
        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.Plugin, "plugin.il2cpp_thread_probe.armed"),
            ("main_thread_id", BppCoroutines.MainThreadId),
            ("il2cpp_attached", IsAttachedToIl2Cpp())
        );
    }

    [Conditional("BPP_ANDROID")]
    internal static void Check(
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = ""
    )
    {
        var mainThreadId = BppCoroutines.MainThreadId;
        var currentThreadId = Environment.CurrentManagedThreadId;
        if (mainThreadId == 0 || currentThreadId == mainThreadId)
            return;

        var site = System.IO.Path.GetFileNameWithoutExtension(file) + "." + member;
        if (!ReportedSites.TryAdd(site, 0))
            return;

        try
        {
            var thread = Thread.CurrentThread;
            BppLog.WarnEvent(
                new BppLogEvent(
                    BppLogFeatureScope.Plugin,
                    "plugin.il2cpp_thread_probe.off_main_thread",
                    storm: ["site"]
                ),
                ("site", site),
                ("thread_id", currentThreadId),
                ("thread_name", thread.Name),
                ("thread_pool", thread.IsThreadPoolThread),
                ("il2cpp_attached", IsAttachedToIl2Cpp()),
                ("stack", new StackTrace(1, false).ToString())
            );
        }
        catch
        {
            // A diagnostic must never change the behavior it observes.
        }
    }

    private static bool IsAttachedToIl2Cpp()
    {
#if BPP_ANDROID
        return Il2CppInterop.Runtime.IL2CPP.il2cpp_thread_current() != IntPtr.Zero;
#else
        return true;
#endif
    }
}
