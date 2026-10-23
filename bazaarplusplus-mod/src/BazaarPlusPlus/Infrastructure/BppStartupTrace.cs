#nullable enable
namespace BazaarPlusPlus.Infrastructure;

/// <summary>
/// Append-only startup trace that survives an interop abort.
///
/// The IL2CPP interop layer calls <c>abort()</c> rather than raising a managed exception, and the
/// BepInEx log writer buffers, so the last lines before a crash are routinely lost. This writes
/// straight to a file with no buffering, which is what makes the failing statement identifiable.
/// </summary>
internal static class BppStartupTrace
{
    public static void Write(string step)
    {
        // No lock and no shared writer. Tracing runs on whichever thread hit the step, including
        // worker threads; a lock here can stall the main thread behind a blocked worker and turn
        // diagnostics into the hang it was meant to find. AppendAllText opens the file per call,
        // which is what makes that impossible.
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "bpp-startup-trace.log"),
                $"{DateTime.UtcNow:HH:mm:ss.fff} {step}{Environment.NewLine}"
            );
        }
        catch (Exception)
        {
            // Tracing must never be the reason startup fails.
        }
    }
}
