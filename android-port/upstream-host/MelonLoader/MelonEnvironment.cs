namespace MelonLoader.Utils;

/// <summary>
/// MelonLoader's directory layout. <see cref="MelonCompatHost"/> sets the base directory before
/// any melon starts; the rest derive from it as they do under MelonLoader.
/// </summary>
public static class MelonEnvironment
{
    private static string? _melonBaseDirectory;

    public static string MelonBaseDirectory =>
        _melonBaseDirectory
        ?? throw new InvalidOperationException("MelonCompatHost has not set the base directory.");

    public static string UserDataDirectory => Path.Combine(MelonBaseDirectory, "UserData");

    public static string UserLibsDirectory => Path.Combine(MelonBaseDirectory, "UserLibs");

    public static string ModsDirectory => Path.Combine(MelonBaseDirectory, "Mods");

    public static string PluginsDirectory => Path.Combine(MelonBaseDirectory, "Plugins");

    internal static void SetBaseDirectory(string melonBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(melonBaseDirectory);
        _melonBaseDirectory = Path.GetFullPath(melonBaseDirectory);
        Directory.CreateDirectory(UserDataDirectory);
    }
}
