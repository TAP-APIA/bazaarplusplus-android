#nullable enable
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using BazaarPlusPlus.Storage.Paths;

namespace BazaarPlusPlus.Core.Paths;

/// <summary>
/// Resolves the BPP data root under BepInEx's game root.
///
/// On the Android port BepInEx points its game root at a writable external directory
/// (<c>/sdcard/BepInEx_Android/&lt;package&gt;</c>), but a read-only or misconfigured root would make
/// every history/config write fail at runtime rather than at startup. The probe below keeps that
/// failure local and falls back to Unity's per-app storage, which is always writable.
///
/// On NextBep the game root is not the external directory but the per-launch code-cache directory
/// (<c>/data/user/0/com.bepinex.android/app_bepinex_runtime/&lt;package&gt;</c>), which is app-private
/// and therefore writable. That is a legitimate outcome, but the resulting root is invisible to a
/// search of shared storage, so the resolution is written once at startup: which candidate won,
/// the absolute path, and why the losing candidate was rejected.
/// </summary>
internal sealed class BepInExPathProvider : IPathProvider
{
    // Not a storm: this runs once per process, and a suppressed line would hide the answer.
    private static readonly BppLogEvent DataRootResolved = new(
        BppLogFeatureScope.Plugin,
        "plugin.data_root.resolved"
    );

    public string? DataRootDirectoryPath { get; private set; }

    /// <summary>Why <see cref="DataRootDirectoryPath"/> points where it does, for the log.</summary>
    public string ResolutionSource { get; private set; } = "uninitialized";

    public void Initialize()
    {
        var candidate = System.IO.Path.Combine(
            BepInEx.Paths.GameRootPath,
            PathConstants.DataRootDirectoryName
        );

        if (CanCreate(candidate, out var candidateFailure))
        {
            Publish(candidate, "bepinex-game-root", probeFailure: null);
            return;
        }

        // NextBep keeps the game root in a per-launch runtime cache. A stable sibling under the
        // same app-private data directory survives runtime-cache churn, so prefer it over Unity's
        // persistent data path, which the launcher redirects to read-only external storage.
        var stable = AppPrivateDataRoot();
        string? stableFailure = null;
        if (stable != null && !string.Equals(stable, candidate, StringComparison.Ordinal))
        {
            if (CanCreate(stable, out stableFailure))
            {
                Publish(stable, "app-private-data", probeFailure: candidateFailure);
                return;
            }
        }

        var fallback = System.IO.Path.Combine(
            UnityEngine.Application.persistentDataPath,
            PathConstants.DataRootDirectoryName
        );

        // Adopting the fallback even when its probe also fails is the pre-existing behaviour: the
        // failure then surfaces at the writer instead of at startup. The probe result is reported
        // so that path is not mistaken for a verified-writable root.
        var fallbackWritable = CanCreate(fallback, out var fallbackFailure);
        Publish(
            fallback,
            "unity-persistent-data",
            fallbackWritable ? null : (fallbackFailure ?? stableFailure ?? candidateFailure)
        );
    }

    /// <summary>
    /// The app-private data directory for this game, or null when the game root is not the NextBep
    /// runtime cache. Both live under the same <c>/data/user/0/&lt;launcher&gt;/</c> parent, so only
    /// the <c>app_bepinex_runtime</c> segment differs from <c>files</c>.
    /// </summary>
    private static string? AppPrivateDataRoot()
    {
        var gameRoot = BepInEx.Paths.GameRootPath;
        if (string.IsNullOrWhiteSpace(gameRoot))
            return null;

        var runtimeDirectory = System.IO.Directory.GetParent(gameRoot.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar
        ));
        var appDirectory = runtimeDirectory?.Parent;
        if (
            runtimeDirectory == null
            || appDirectory == null
            || !string.Equals(
                runtimeDirectory.Name,
                "app_bepinex_runtime",
                StringComparison.Ordinal
            )
        )
            return null;

        return System.IO.Path.Combine(
            appDirectory.FullName,
            "files",
            System.IO.Path.GetFileName(gameRoot.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar
            )),
            PathConstants.DataRootDirectoryName
        );
    }

    private void Publish(string dataRoot, string source, string? probeFailure)
    {
        DataRootDirectoryPath = dataRoot;
        ResolutionSource = source;

        BppLog.InfoEvent(
            DataRootResolved,
            ("source", source),
            ("data_root", dataRoot),
            ("game_root", BepInEx.Paths.GameRootPath),
            ("persistent_data_path", UnityEngine.Application.persistentDataPath),
            ("probe_failure", probeFailure ?? "none")
        );

        // The BepInEx log writer buffers, so the same fact goes to the unbuffered startup trace.
        BppStartupTrace.Write($"data-root:{source}:{dataRoot}");
        if (probeFailure != null)
            BppStartupTrace.Write($"data-root:probe-failed:{probeFailure}");
    }

    private static bool CanCreate(string directoryPath, out string? failure)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directoryPath);
            var probe = System.IO.Path.Combine(directoryPath, ".bpp-write-probe");
            System.IO.File.WriteAllText(probe, string.Empty);
            System.IO.File.Delete(probe);
            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            // The type and message are what identify a storage-permission or read-only failure.
            failure = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }
}
