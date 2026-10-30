using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BazaarPlusPlus.OfficialPayload;
using Xunit;

public sealed class OfficialPayloadInstallTests : IDisposable
{
    private const string Version = "6.0.2";
    private const string Digest = "f41fcb027911e235fb48424eab16fd2698266cd4ab9ecba5605aae19646f7ae1";

    private readonly string _root = Directory.CreateTempSubdirectory("bpp-official-payload").FullName;
    private readonly List<string> _lines = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string InstallRoot => Path.Combine(_root, "install");

    private string Updates => Path.Combine(InstallRoot, "BazaarPlusPlusV6", "Updates");

    [Fact]
    public void Bundled_payload_is_staged_when_nothing_is_installed()
    {
        Install().StageBundled(Bundle(Version));

        var staged = Path.Combine(Updates, Version);
        Assert.True(File.Exists(Path.Combine(staged, "manifest.txt")));
        Assert.True(File.Exists(Path.Combine(staged, "manifest.txt.minisig")));
        Assert.True(File.Exists(Path.Combine(staged, "files", "MelonLoader", "Mods", "BazaarPlusPlus.version")));
        Assert.Single(Directory.GetDirectories(Updates));
    }

    [Fact]
    public void Bundled_payload_not_newer_than_the_installed_one_is_not_staged()
    {
        Installed("6.0.3");
        Install().StageBundled(Bundle(Version));
        Assert.False(Directory.Exists(Updates));
    }

    [Fact]
    public void Bundled_payload_newer_than_the_installed_one_is_staged()
    {
        Installed("6.0.1");
        Install().StageBundled(Bundle(Version));
        Assert.True(Directory.Exists(Path.Combine(Updates, Version)));
    }

    [Fact]
    public void Rejected_bundled_version_is_not_staged_again()
    {
        new RejectedVersions(Path.Combine(InstallRoot, "BazaarPlusPlusV6")).Add(Version, "launch_unconfirmed");
        Install().StageBundled(Bundle(Version));
        Assert.False(Directory.Exists(Updates));
    }

    [Fact]
    public void A_bundle_writing_outside_the_payload_directories_is_refused()
    {
        Install().StageBundled(Bundle(Version, ("files/BepInEx/plugins/Evil.dll", "x")));
        Assert.False(Directory.Exists(Path.Combine(Updates, Version)));
        Assert.Contains(_lines, line => line.Contains("official_payload.bundled.failed"));
    }

    [Fact]
    public void An_unsigned_staged_payload_is_deleted_and_recorded()
    {
        var install = Install();
        install.StageBundled(Bundle(Version));
        install.ScreenStaged();

        Assert.False(Directory.Exists(Path.Combine(Updates, Version)));
        Assert.True(new RejectedVersions(install.DataRoot).Contains(Version));
        Assert.Contains(_lines, line => line.Contains("official_payload.staged.rejected"));
    }

    [Fact]
    public void A_staged_payload_not_newer_than_the_installed_one_is_left_to_the_updater()
    {
        Installed(Version);
        var staged = Path.Combine(Updates, Version);
        Directory.CreateDirectory(staged);
        var install = Install();
        install.ScreenStaged();
        Assert.True(Directory.Exists(staged));
        Assert.False(new RejectedVersions(install.DataRoot).Contains(Version));
    }

    [Fact]
    public void Installed_version_comes_from_the_version_file_without_a_manifest()
    {
        Installed("6.0.2.prod");
        Assert.Equal("6.0.2", Install().InstalledVersion);
    }

    [Fact]
    public void A_payload_that_never_reaches_the_main_menu_is_given_up()
    {
        Installed(Version);
        var install = Install();
        for (var i = 0; i < LaunchGuard.MaxUnconfirmedLaunches; i++)
            Assert.True(install.BeginLaunch());

        Assert.False(install.BeginLaunch());
        Assert.True(new RejectedVersions(install.DataRoot).Contains(Version));
        Assert.Null(install.InstalledVersion);
    }

    [Fact]
    public void A_confirmed_payload_keeps_loading()
    {
        Installed(Version);
        var install = Install();
        Assert.True(install.BeginLaunch());
        install.ConfirmLaunch();
        for (var i = 0; i < LaunchGuard.MaxUnconfirmedLaunches + 2; i++)
            Assert.True(install.BeginLaunch());
    }

    [Fact]
    public void Nothing_installed_loads_nothing()
    {
        Assert.False(Install().BeginLaunch());
    }

    private OfficialPayloadInstall Install() =>
        new(InstallRoot, Array.Empty<string>(), _lines.Add, _lines.Add, _lines.Add);

    private void Installed(string versionText)
    {
        var mods = Path.Combine(InstallRoot, "MelonLoader", "Mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "BazaarPlusPlus.version"), versionText);
    }

    private string Bundle(string version, params (string Name, string Content)[] extra)
    {
        var versionFile = Encoding.UTF8.GetBytes(version + ".prod");
        var manifest =
            "bpp-managed-payload-manifest 1\n"
            + $"version {version}\n"
            + "platform android-aarch64\n"
            + "history-database-user-version 1\n"
            + $"non-managed-payload-digest {Digest}\n"
            + $"file {Convert.ToHexStringLower(SHA256.HashData(versionFile))} {versionFile.Length} MelonLoader/Mods/BazaarPlusPlus.version\n";
        var path = Path.Combine(_root, $"bundle-{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(archive, "manifest.txt", Encoding.UTF8.GetBytes(manifest));
            Add(archive, "manifest.txt.minisig", Encoding.UTF8.GetBytes("untrusted comment: test\n"));
            Add(archive, "files/MelonLoader/Mods/BazaarPlusPlus.version", versionFile);
            foreach (var (name, content) in extra)
                Add(archive, name, Encoding.UTF8.GetBytes(content));
        }
        return path;
    }

    private static void Add(ZipArchive archive, string name, byte[] content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(content);
    }
}
