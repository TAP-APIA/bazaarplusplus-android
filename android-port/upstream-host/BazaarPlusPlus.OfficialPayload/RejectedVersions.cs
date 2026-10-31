namespace BazaarPlusPlus.OfficialPayload;

/// <summary>
/// Payload versions this device will not take again, one <c>version reason</c> per line under the
/// Data Root. The launcher reads the same file before it downloads a release, so a refused
/// payload is not fetched on every start.
/// </summary>
public sealed class RejectedVersions
{
    public const string FileName = "bpp-official-payload.rejected";

    private readonly string _path;

    public RejectedVersions(string dataRoot) => _path = Path.Combine(dataRoot, FileName);

    public bool Contains(string version) =>
        Read().Any(line => line.Split(' ', 2)[0] == version);

    public void Add(string version, string reason)
    {
        if (Contains(version))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.AppendAllText(_path, $"{version} {reason}\n");
    }

    private IEnumerable<string> Read() =>
        File.Exists(_path)
            ? File.ReadAllLines(_path).Where(line => line.Length > 0)
            : Array.Empty<string>();
}
