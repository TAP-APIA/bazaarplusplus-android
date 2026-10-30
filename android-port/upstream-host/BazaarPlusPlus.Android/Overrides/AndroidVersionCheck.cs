using BazaarPlusPlus.Game.Lobby;

namespace BazaarPlusPlus.Android.Lobby;

// Replaces upstream Lobby/AndroidVersionCheck.cs (README.md, Overrides). Upstream's check reads
// upstream's android-aarch64 Platform Release Manifest, stages upstream's Managed Payload for its
// Updater and offers upstream's APK. None of that fits this build: the launcher owns updates
// (BppLauncherUpdateChecker), so this keeps upstream's surface and does nothing.

/// <summary>What the game-update row says: the last check's answer about the APK.</summary>
internal enum AndroidGameUpdateStatus
{
    /// <summary>No check has answered yet this session.</summary>
    Checking,

    /// <summary>The last check failed, so whether the game is current is unknown.</summary>
    CheckFailed,

    /// <summary>This copy needs no new APK.</summary>
    Current,

    /// <summary>The published APK carries a newer game.</summary>
    GameUpdated,

    /// <summary>Same game, but the BPP runtime inside the APK changed.</summary>
    RuntimeChanged,
}

/// <summary>Upstream's main-menu release check, inert: it never touches the network.</summary>
internal sealed class AndroidVersionCheck : IDisposable
{
    public AndroidVersionCheck(
        string installRoot,
        string dataRoot,
        string gameVersion,
        Uri? manifestOverride = null
    ) { }

    public static Uri? DevManifest(int port) => null;

    public AndroidApkUpdate? ApkUpdate => null;

    public string? LatestVersion => null;

    /// <summary>The launcher, not this check, decides about new APKs.</summary>
    public AndroidGameUpdateStatus GameUpdateStatus => AndroidGameUpdateStatus.Current;

    public void Begin() { }

    public void Tick() { }

    public void Dispose() { }
}
