using BazaarPlusPlus.Android.Host;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace BazaarPlusPlus.BepInExBridge;

/// <summary>
/// Starts upstream's Android Host (<see cref="AndroidMod"/>) as MelonLoader would, inside the
/// launcher's BepInEx: its log lines go to this plugin's log source, its MelonLoader base
/// directory is <c>MelonLoader/</c> under the game root, so its Data Root is the game root's
/// <c>BazaarPlusPlusV6</c> (upstream AndroidPaths), the directory this fork's plugin used.
/// </summary>
[BepInPlugin(Guid, "BazaarPlusPlus", "1.0.0")]
public sealed class BridgePlugin : BasePlugin
{
    public const string Guid = "com.bazaarplusplus.android.upstream";

    private static MelonCompatHost? _host;

    internal static MelonCompatHost? Host => _host;

    public override void Load()
    {
        var log = Log;
        MelonLogger.Sink = (name, level, text) => Write(log, name, level, text);
        var melonBase = Path.Combine(Paths.GameRootPath, "MelonLoader");
        AndroidSqlite.Load(log);
        try
        {
            _host = MelonCompatHost.Create(typeof(AndroidMod).Assembly, melonBase);
        }
        catch (Exception exception)
        {
            log.LogError($"upstream melon could not be created: {exception}");
            return;
        }
        log.LogInfo(
            $"bridge.loaded melon={_host.Melon.Info.Name} version={_host.Melon.Info.Version} melon_base={melonBase}"
        );
        AddComponent<BridgeBehaviour>();
        SceneManager.sceneLoaded += (UnityAction<Scene, LoadSceneMode>)OnSceneLoaded;
        SceneManager.sceneUnloaded += (UnityAction<Scene>)OnSceneUnloaded;
        _host.Initialize();
    }

    public override bool Unload()
    {
        _host?.Quit();
        return true;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) =>
        _host?.SceneLoaded(scene.buildIndex, scene.name);

    private static void OnSceneUnloaded(Scene scene) =>
        _host?.SceneUnloaded(scene.buildIndex, scene.name);

    private static void Write(ManualLogSource log, string name, MelonLogLevel level, string text)
    {
        var line = name == "BazaarPlusPlus" ? text : $"[{name}] {text}";
        switch (level)
        {
            case MelonLogLevel.Warning:
                log.LogWarning(line);
                return;
            case MelonLogLevel.Error:
                log.LogError(line);
                return;
            default:
                log.LogInfo(line);
                return;
        }
    }
}

/// <summary>The frame source: Unity calls it, it calls the melon.</summary>
public sealed class BridgeBehaviour : MonoBehaviour
{
    public BridgeBehaviour(IntPtr pointer)
        : base(pointer) { }

    private void Update() => BridgePlugin.Host?.Update();

    private void LateUpdate() => BridgePlugin.Host?.LateUpdate();

    private void OnApplicationQuit() => BridgePlugin.Host?.Quit();
}
