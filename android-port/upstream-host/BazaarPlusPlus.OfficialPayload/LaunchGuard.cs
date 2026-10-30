namespace BazaarPlusPlus.OfficialPayload;

/// <summary>
/// Counts starts of the installed payload that never reached the main menu. A Harmony patch that
/// crashes on this runtime shows up only when it runs, so after <see cref="MaxUnconfirmedLaunches"/>
/// such starts in a row the version is given up: the caller then stops loading it.
/// </summary>
public sealed class LaunchGuard
{
    public const string FileName = "bpp-official-payload.launch";
    public const int MaxUnconfirmedLaunches = 3;

    private readonly string _path;

    public LaunchGuard(string dataRoot) => _path = Path.Combine(dataRoot, FileName);

    /// <summary>Records one more start of <paramref name="version"/>; false once it is given up.</summary>
    public bool Begin(string version)
    {
        var (recorded, attempts, confirmed) = Read();
        if (recorded == version && confirmed)
            return true;
        attempts = recorded == version ? attempts : 0;
        if (attempts >= MaxUnconfirmedLaunches)
            return false;
        Write(version, attempts + 1, false);
        return true;
    }

    /// <summary>The main menu loaded: this version works on this device.</summary>
    public void Confirm(string version) => Write(version, 0, true);

    private (string? Version, int Attempts, bool Confirmed) Read()
    {
        if (!File.Exists(_path))
            return (null, 0, false);
        var parts = File.ReadAllText(_path).Trim().Split(' ');
        return parts.Length == 3 && int.TryParse(parts[1], out var attempts)
            ? (parts[0], attempts, parts[2] == "confirmed")
            : (null, 0, false);
    }

    private void Write(string version, int attempts, bool confirmed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, $"{version} {attempts} {(confirmed ? "confirmed" : "pending")}");
    }
}
