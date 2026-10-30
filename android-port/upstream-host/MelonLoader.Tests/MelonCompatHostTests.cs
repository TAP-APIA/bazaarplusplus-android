using MelonLoader;
using MelonLoader.Utils;
using Xunit;

[assembly: MelonInfo(typeof(RecordingMelon), "Recording", "1.2.3", "tests")]

public sealed class RecordingMelon : MelonMod
{
    public static readonly List<string> Calls = [];
    public static bool ThrowOnUpdate;

    public override void OnInitializeMelon() => Calls.Add($"init:{Info.Name}:{Info.Version}");

    public override void OnLateInitializeMelon() => Calls.Add("late-init");

    public override void OnUpdate()
    {
        Calls.Add("update");
        if (ThrowOnUpdate)
            throw new InvalidOperationException("boom");
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName) =>
        Calls.Add($"scene:{buildIndex}:{sceneName}");

    public override void OnApplicationQuit() => Calls.Add("quit");

    public override void OnDeinitializeMelon() => Calls.Add("deinit");
}

[Collection("melon")]
public sealed class MelonCompatHostTests : IDisposable
{
    private readonly string _base = Path.Combine(
        Directory.CreateTempSubdirectory("melon-compat").FullName,
        "MelonLoader"
    );
    private readonly List<(string Name, MelonLogLevel Level, string Text)> _log = [];

    public MelonCompatHostTests()
    {
        RecordingMelon.Calls.Clear();
        RecordingMelon.ThrowOnUpdate = false;
        MelonLogger.Sink = (name, level, text) => _log.Add((name, level, text));
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_base)!, recursive: true);

    [Fact]
    public void Runs_the_melon_named_by_the_assembly_in_MelonLoader_order()
    {
        var host = MelonCompatHost.Create(typeof(RecordingMelon).Assembly, _base);
        host.Update(); // before Initialize: ignored
        host.Initialize();
        host.Update();
        host.Update();
        host.SceneLoaded(2, "MainMenuScene");
        host.Quit();
        host.Quit();
        host.Update(); // after Quit: ignored

        Assert.Equal(
            [
                "init:Recording:1.2.3",
                "late-init",
                "update",
                "update",
                "scene:2:MainMenuScene",
                "quit",
                "deinit",
            ],
            RecordingMelon.Calls
        );
    }

    [Fact]
    public void Sets_the_MelonLoader_directories_under_the_base()
    {
        MelonCompatHost.Create(typeof(RecordingMelon).Assembly, _base);
        Assert.Equal(_base, MelonEnvironment.MelonBaseDirectory);
        Assert.Equal(Path.Combine(_base, "UserData"), MelonEnvironment.UserDataDirectory);
        Assert.True(Directory.Exists(MelonEnvironment.UserDataDirectory));
    }

    [Fact]
    public void A_throwing_callback_is_logged_and_does_not_stop_the_next_frame()
    {
        var host = MelonCompatHost.Create(typeof(RecordingMelon).Assembly, _base);
        host.Initialize();
        RecordingMelon.ThrowOnUpdate = true;
        host.Update();
        host.Update();

        Assert.Equal(2, RecordingMelon.Calls.Count(call => call == "update"));
        Assert.Equal(2, _log.Count(line => line.Level == MelonLogLevel.Error));
        Assert.All(
            _log.Where(line => line.Level == MelonLogLevel.Error),
            line =>
            {
                Assert.Equal("Recording", line.Name);
                Assert.Contains("OnUpdate failed", line.Text);
                Assert.Contains("boom", line.Text);
            }
        );
    }

    [Fact]
    public void The_melon_gets_its_own_logger()
    {
        var host = MelonCompatHost.Create(typeof(RecordingMelon).Assembly, _base);
        host.Melon.LoggerInstance.Warning("careful");
        Assert.Equal(("Recording", MelonLogLevel.Warning, "careful"), _log.Single());
    }

    [Fact]
    public void An_assembly_without_MelonInfo_is_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MelonCompatHost.Create(typeof(object).Assembly, _base)
        );
        Assert.Contains("MelonInfo", error.Message);
    }
}
