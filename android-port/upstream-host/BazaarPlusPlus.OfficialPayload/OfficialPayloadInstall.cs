using System.IO.Compression;
using BazaarPlusPlus.ManagedPayload;
using BazaarPlusPlus.Updater;

namespace BazaarPlusPlus.OfficialPayload;

/// <summary>
/// Upstream's Android Managed Payload installed under <c>&lt;install root&gt;/MelonLoader</c>, as
/// LemonLoader lays it out. Each start: stage the payload the APK bundles when it is newer than
/// the installed one, refuse every staged payload that fails its signature or does not bind on
/// this device, let upstream's Updater swap in the newest that passes, then
/// <see cref="BeginLaunch"/> before the mod loads. The launcher stages downloaded releases the
/// same way, under the Data Root's <c>Updates/&lt;version&gt;</c>.
/// </summary>
public sealed class OfficialPayloadInstall
{
    /// <summary>The bundled payload, beside the bridge in <c>BepInEx/plugins</c>.</summary>
    public const string BundledFileName = "BazaarPlusPlus.OfficialPayload.zip";

    private readonly string _installRoot;
    private readonly IReadOnlyList<string> _hostDirectories;
    private readonly MinisignPublicKey _publicKey;
    private readonly UpdaterLog _log;
    private readonly RejectedVersions _rejected;
    private readonly LaunchGuard _guard;

    public OfficialPayloadInstall(
        string installRoot,
        IReadOnlyList<string> hostDirectories,
        Action<string> info,
        Action<string> warn,
        Action<string> error
    )
        : this(installRoot, hostDirectories, ReleaseSigningKey.PublicKey, new UpdaterLog(info, warn, error)) { }

    internal OfficialPayloadInstall(
        string installRoot,
        IReadOnlyList<string> hostDirectories,
        MinisignPublicKey publicKey,
        UpdaterLog log
    )
    {
        _installRoot = installRoot;
        _hostDirectories = hostDirectories;
        _publicKey = publicKey;
        _log = log;
        DataRoot = Path.Combine(installRoot, UpdaterRun.DataRootDirectoryName);
        _rejected = new RejectedVersions(DataRoot);
        _guard = new LaunchGuard(DataRoot);
    }

    public string DataRoot { get; }

    public string MelonBaseDirectory => Path.Combine(_installRoot, "MelonLoader");

    public string ModsDirectory => Path.Combine(MelonBaseDirectory, "Mods");

    public string UserLibsDirectory => Path.Combine(MelonBaseDirectory, "UserLibs");

    private string UpdatesDirectory =>
        Path.Combine(DataRoot, StagedManagedPayload.UpdatesDirectoryName);

    private static ManagedPayloadHost Host => ManagedPayloadHost.MelonLoader;

    /// <summary>The installed payload's version, or null when none is installed.</summary>
    public string? InstalledVersion
    {
        get
        {
            var manifest = InstalledManifest();
            if (manifest != null)
                return manifest.Version;
            var versionFile = Resolve(Host.VersionFilePath);
            return
                File.Exists(versionFile)
                && ManagedPayloadVersion.TryParse(File.ReadAllText(versionFile), out var version)
                ? version.ToString(3)
                : null;
        }
    }

    /// <summary>Stages the bundled payload when it is newer than the installed one.</summary>
    public void StageBundled(string zipPath)
    {
        string? partial = null;
        try
        {
            if (!File.Exists(zipPath))
            {
                _log.Warn("official_payload.bundled.missing", ("path", zipPath));
                return;
            }
            using var archive = ZipFile.OpenRead(zipPath);
            var manifestEntry =
                archive.GetEntry(ManagedPayloadManifest.FileName)
                ?? throw new InvalidDataException("manifest missing");
            ManagedPayloadManifest manifest;
            using (var stream = manifestEntry.Open())
            using (var reader = new StreamReader(stream))
                manifest = ManagedPayloadManifest.Parse(reader.ReadToEnd());

            var version = manifest.Version;
            if (_rejected.Contains(version))
            {
                _log.Info("official_payload.bundled.rejected_before", ("version", version));
                return;
            }
            if (!IsNewerThanInstalled(version))
                return;
            var target = Path.Combine(UpdatesDirectory, version);
            if (Directory.Exists(target))
                return;

            partial = Path.Combine(UpdatesDirectory, "." + version + ".partial");
            if (Directory.Exists(partial))
                Directory.Delete(partial, recursive: true);
            Directory.CreateDirectory(partial);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                    continue;
                var relative = StagedRelativePath(entry.FullName);
                var destination = Path.Combine(new[] { partial }.Concat(relative.Split('/')).ToArray());
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination);
            }
            Directory.Move(partial, target);
            _log.Info(
                "official_payload.bundled.staged",
                ("version", version),
                ("installed", InstalledVersion ?? "none")
            );
        }
        catch (Exception exception)
        {
            _log.Error("official_payload.bundled.failed", exception, ("path", zipPath));
            if (partial != null)
                TryDelete(partial);
        }
    }

    /// <summary>
    /// Deletes every staged payload newer than the installed one that is unsigned, built for
    /// another non-managed payload, or does not bind on this device, and records it as rejected.
    /// </summary>
    public void ScreenStaged()
    {
        if (!Directory.Exists(UpdatesDirectory))
            return;
        var installed = InstalledManifest();
        foreach (var directory in Directory.GetDirectories(UpdatesDirectory))
        {
            var version = Path.GetFileName(directory);
            if (version.Split('.').Length != 3 || !IsNewerThanInstalled(version))
                continue;
            var failure = Screen(directory, installed);
            if (failure == null)
            {
                _log.Info("official_payload.staged.accepted", ("version", version));
                continue;
            }
            _log.Warn("official_payload.staged.rejected", ("version", version), ("reason_code", failure));
            _rejected.Add(version, failure);
            TryDelete(directory);
        }
    }

    private string? Screen(string directory, ManagedPayloadManifest? installed)
    {
        if (
            !StagedManagedPayload.TryVerify(
                directory,
                Host,
                UpdaterRun.AndroidPlatformKey,
                _publicKey,
                out var manifest,
                out var failure
            )
        )
            return failure;
        if (installed != null)
        {
            if (manifest!.HistoryDatabaseUserVersion != installed.HistoryDatabaseUserVersion)
                return "history_database_mismatch";
            if (manifest.NonManagedPayloadDigest != installed.NonManagedPayloadDigest)
                return "non_managed_payload_mismatch";
        }
        try
        {
            return PayloadCompatibility.Check(
                Path.Combine(directory, ManagedPayloadManifest.FilesDirectory, "MelonLoader"),
                _hostDirectories
            );
        }
        catch (Exception exception)
        {
            return "compatibility_check_failed:" + exception.GetType().Name;
        }
    }

    /// <summary>Upstream's Updater: swaps in the newest staged payload that still verifies.</summary>
    public void Apply() =>
        UpdaterRun.Run(_installRoot, Host, UpdaterRun.AndroidPlatformKey, _log);

    /// <summary>
    /// Counts this start of the installed payload. False when there is nothing to load, or when it
    /// has failed to reach the main menu too often: it is then rejected and uninstalled down to its
    /// version marker, so the next start stages the bundled payload again.
    /// </summary>
    public bool BeginLaunch()
    {
        var version = InstalledVersion;
        if (version == null)
        {
            _log.Warn("official_payload.not_installed", ("mods", ModsDirectory));
            return false;
        }
        if (_guard.Begin(version))
            return true;
        _log.Warn(
            "official_payload.launch.given_up",
            ("version", version),
            ("attempts", LaunchGuard.MaxUnconfirmedLaunches)
        );
        _rejected.Add(version, "launch_unconfirmed");
        TryDelete(Resolve(Host.InstalledManifestPath));
        TryDelete(Resolve(Host.VersionFilePath));
        return false;
    }

    /// <summary>The main menu loaded with the installed payload.</summary>
    public void ConfirmLaunch()
    {
        var version = InstalledVersion;
        if (version != null)
            _guard.Confirm(version);
    }

    private bool IsNewerThanInstalled(string version)
    {
        if (!ManagedPayloadVersion.TryParse(version, out var candidate))
            return false;
        var installed = InstalledVersion;
        return installed == null
            || !ManagedPayloadVersion.TryParse(installed, out var current)
            || candidate > current;
    }

    private ManagedPayloadManifest? InstalledManifest()
    {
        var path = Resolve(Host.InstalledManifestPath);
        if (!File.Exists(path))
            return null;
        try
        {
            return ManagedPayloadManifest.Parse(File.ReadAllBytes(path));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // The zip holds exactly the staged layout: the manifest, its signature, and files under
    // files/ that the MelonLoader host may write.
    private static string StagedRelativePath(string entryName)
    {
        if (
            entryName is ManagedPayloadManifest.FileName or ManagedPayloadManifest.SignatureFileName
        )
            return entryName;
        var prefix = ManagedPayloadManifest.FilesDirectory + "/";
        if (
            entryName.StartsWith(prefix, StringComparison.Ordinal)
            && Host.MayWrite(entryName[prefix.Length..])
        )
            return entryName;
        throw new InvalidDataException($"unexpected entry {entryName}");
    }

    private string Resolve(string installRelative) =>
        Path.Combine(new[] { _installRoot }.Concat(installRelative.Split('/')).ToArray());

    private void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Warn("official_payload.cleanup.failed", ("path", path), ("error", exception.Message));
        }
    }
}
