using BazaarPlusPlus.Game.Lobby;

namespace BazaarPlusPlus.Android.Lobby;

// Replaces upstream Lobby/AndroidApkUpdatePrompt.cs (README.md, Overrides). Upstream's prompt
// sends players to upstream's BPP-signed APK, which cannot install over this launcher; the
// launcher's own update check offers this build's APK instead.

/// <summary>Upstream's new-APK popup, inert.</summary>
internal sealed class AndroidApkUpdatePrompt
{
    public AndroidApkUpdatePrompt(
        AndroidVersionCheck check,
        string dataRoot,
        Action<AndroidApkUpdate> download,
        Func<bool> panelOpen
    ) { }

    public void GameRequiresUpdate() { }

    public void Tick() { }
}
