#if BPP_ANDROID
using System.Runtime.InteropServices;
using BazaarPlusPlus.Infrastructure.Logging;

namespace BazaarPlusPlus.Infrastructure;

internal static class AndroidNativeLibraries
{
    // Keep the provider handle for the process lifetime; native SQLite owns live callbacks.
    private static IntPtr _sqlite;

    internal static void Initialize()
    {
        try
        {
            var coreClr = System.IO.File.ReadLines("/proc/self/maps")
                .Select(line => line.IndexOf('/') is var start && start >= 0 ? line[start..].Trim() : string.Empty)
                .First(path => path.EndsWith("/libcoreclr.so", StringComparison.Ordinal));
            var sqlite = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(coreClr)!, "libe_sqlite3.so");
            _sqlite = NativeLibrary.Load(sqlite);
            NativeLibrary.SetDllImportResolver(
                typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly,
                (name, _, _) => name is "e_sqlite3" or "libe_sqlite3.so" ? _sqlite : IntPtr.Zero
            );
            BppLog.InfoEvent(
                new BppLogEvent(BppLogFeatureScope.Plugin, "plugin.android.sqlite.loaded"),
                ("native_path", sqlite)
            );
        }
        catch (Exception ex)
        {
            BppLog.WarnEvent(
                new BppLogEvent(BppLogFeatureScope.Plugin, "plugin.android.sqlite.unavailable"),
                ex,
                ("reason_code", PluginLogReasonCode.NativeLibraryLoadFailed)
            );
        }
    }
}
#endif
