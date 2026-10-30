namespace MelonLoader;

/// <summary>Names a melon's entry type and identity, as MelonLoader's attribute does.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class MelonInfoAttribute(
    Type type,
    string name,
    string version,
    string author,
    string? downloadLink = null
) : Attribute
{
    public Type SystemType { get; } = type;

    public string Name { get; } = name;

    public string Version { get; } = version;

    public string Author { get; } = author;

    public string? DownloadLink { get; } = downloadLink;
}
