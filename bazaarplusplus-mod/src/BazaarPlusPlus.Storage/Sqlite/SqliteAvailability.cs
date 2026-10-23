#nullable enable
namespace BazaarPlusPlus.Storage.Sqlite;

/// <summary>
/// One-shot probe for whether the SQLite native library can actually be loaded.
///
/// On the Android port the bundled <c>libe_sqlite3.so</c> lives on shared storage, which the
/// Android linker refuses to <c>dlopen</c> from (namespace isolation). Probing once turns that into
/// a flag the stores can consult, instead of a <see cref="TypeInitializationException"/> that
/// aborts the whole plugin composition.
/// </summary>
public static class SqliteAvailability
{
    private static readonly Lazy<bool> Probe = new(ProbeCore);

    public static bool IsAvailable => Probe.Value;

    public static string UnavailableReason { get; private set; } = "unknown";

    private static bool ProbeCore()
    {
        try
        {
            SQLitePCL.Batteries_V2.Init();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version();";
            command.ExecuteScalar();
            UnavailableReason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            UnavailableReason = ex.Message;
            return false;
        }
    }
}
