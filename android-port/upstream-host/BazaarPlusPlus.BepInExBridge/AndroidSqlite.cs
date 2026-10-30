using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Logging;

namespace BazaarPlusPlus.BepInExBridge;

/// <summary>
/// Makes <c>e_sqlite3</c> loadable before upstream's record chain opens its database. Upstream's
/// APK carries the library in its own <c>lib/</c>; under the launcher, CoreCLR's linker namespace
/// cannot map it from external storage (android-port/HANDOVER-ANDROID-PORT.md), so this loads it
/// from beside <c>libcoreclr.so</c>, or from a copy staged in the game root, and resolves the
/// payload's provider imports to it. Same approach as the fork plugin's
/// <c>Infrastructure/AndroidNativeLibraries</c>.
/// </summary>
internal static class AndroidSqlite
{
    private const string ProviderAssembly = "SQLitePCLRaw.provider.e_sqlite3.dll";

    // Kept for the process lifetime; native SQLite owns live callbacks.
    private static IntPtr _handle;

    public static void Load(ManualLogSource log, string userLibsDirectory)
    {
        try
        {
            var coreClr = File.ReadLines("/proc/self/maps")
                .Select(line =>
                    line.IndexOf('/') is var start && start >= 0 ? line[start..].Trim() : string.Empty
                )
                .First(path => path.EndsWith("/libcoreclr.so", StringComparison.Ordinal));
            var sqlite = Path.Combine(Path.GetDirectoryName(coreClr)!, "libe_sqlite3.so");
            if (!File.Exists(sqlite))
            {
                sqlite = Path.Combine(Paths.GameRootPath, "libe_sqlite3.so");
                File.Copy(Path.Combine(Paths.PluginPath, "libe_sqlite3.so"), sqlite, overwrite: true);
            }
            _handle = NativeLibrary.Load(sqlite);
            var provider = Assembly.LoadFrom(Path.Combine(userLibsDirectory, ProviderAssembly));
            NativeLibrary.SetDllImportResolver(
                provider,
                (name, _, _) => name is "e_sqlite3" or "libe_sqlite3.so" ? _handle : IntPtr.Zero
            );
            log.LogInfo($"bridge.sqlite.loaded native_path={sqlite}");
        }
        catch (Exception exception)
        {
            // Upstream's record chain then fails to start and logs why; the hooks still run.
            log.LogWarning($"bridge.sqlite.unavailable {exception}");
        }
    }
}
