#if BPP_ANDROID
#nullable enable
using System;
using System.IO;
using BepInEx;
using BazaarPlusPlus.Infrastructure;

namespace BazaarPlusPlus.GameInterop;

/// <summary>
/// The original launcher restores <c>modpacks/bpp/config</c> over <c>BepInEx/config</c> when the
/// game starts. A BPP setting changed in-game must therefore be copied into the modpack copy
/// immediately, or the next launch silently reverts it.
/// </summary>
internal static class AndroidModpackConfigSync
{
    internal static void SyncBazaarPlusPlusConfig()
    {
        try
        {
            var configPath = Path.Combine(Paths.ConfigPath, "BazaarPlusPlus.cfg");
            if (!File.Exists(configPath))
                return;
            var bepinexRoot = Directory.GetParent(Paths.ConfigPath);
            var gameRoot = bepinexRoot?.Parent;
            if (gameRoot == null)
                return;
            var modpackConfig = Path.Combine(
                gameRoot.FullName,
                "modpacks",
                "bpp",
                "config"
            );
            if (!Directory.Exists(modpackConfig))
                return;
            File.Copy(configPath, Path.Combine(modpackConfig, "BazaarPlusPlus.cfg"), true);
        }
        catch (Exception exception)
        {
            BppStartupTrace.Write(
                "config-sync:failed " + exception.GetType().Name + " | " + exception.Message
            );
        }
    }
}
#endif
