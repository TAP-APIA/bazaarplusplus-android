using System.Reflection;
using System.Runtime.Loader;
using BazaarPlusPlus.OfficialPayload;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace BazaarPlusPlus.BepInExBridge;

/// <summary>
/// Runs upstream's own published Android Managed Payload as LemonLoader would, inside the
/// launcher's BepInEx. The payload lives where LemonLoader keeps it, <c>MelonLoader/Mods</c> and
/// <c>MelonLoader/UserLibs</c> under the game root, so its Data Root is the game root's
/// <c>BazaarPlusPlusV6</c> (upstream AndroidPaths), the directory this fork's plugin used.
/// <see cref="OfficialPayloadInstall"/> installs and updates it before anything of it loads.
/// </summary>
[BepInPlugin(Guid, "BazaarPlusPlus", "2.0.0")]
public sealed class BridgePlugin : BasePlugin
{
    public const string Guid = "com.bazaarplusplus.android.upstream";

    private const string MainMenuScene = "MainMenuScene";

    private static MelonCompatHost? _host;
    private static OfficialPayloadInstall? _payload;

    internal static MelonCompatHost? Host => _host;

    public override void Load()
    {
        var log = Log;
        MelonLogger.Sink = (name, level, text) => Write(log, name, level, text);
        try
        {
            LetterBoxPatches.Apply(new HarmonyLib.Harmony(Guid));
            log.LogInfo("bridge.letterbox.patched");
        }
        catch (Exception exception)
        {
            log.LogWarning($"bridge.letterbox.unavailable {exception}");
        }
        var payload = new OfficialPayloadInstall(
            Paths.GameRootPath,
            new[]
            {
                Path.Combine(Paths.BepInExRootPath, "interop"),
                Paths.BepInExAssemblyDirectory,
                Paths.PluginPath,
            },
            log.LogInfo,
            log.LogWarning,
            log.LogError
        );
        payload.StageBundled(Path.Combine(Paths.PluginPath, OfficialPayloadInstall.BundledFileName));
        payload.ScreenStaged();
        payload.Apply();
        if (!payload.BeginLaunch())
            return;
        _payload = payload;

        var mod = LoadMod(payload, log);
        if (mod == null)
            return;
        AndroidSqlite.Load(log, payload.UserLibsDirectory);
        try
        {
            _host = MelonCompatHost.Create(mod, payload.MelonBaseDirectory);
        }
        catch (Exception exception)
        {
            log.LogError($"upstream melon could not be created: {exception}");
            return;
        }
        log.LogInfo(
            $"bridge.loaded melon={_host.Melon.Info.Name} version={_host.Melon.Info.Version} melon_base={payload.MelonBaseDirectory}"
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

    // LemonLoader's order: UserLibs resolve first, then the Mods; the mod is the assembly that
    // names its melon.
    private static Assembly? LoadMod(OfficialPayloadInstall payload, ManualLogSource log)
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var directory in new[] { payload.UserLibsDirectory, payload.ModsDirectory })
            {
                var candidate = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(candidate))
                    return context.LoadFromAssemblyPath(candidate);
            }
            return null;
        };
        try
        {
            foreach (var path in Directory.GetFiles(payload.ModsDirectory, "*.dll"))
            {
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                if (assembly.GetCustomAttribute<MelonInfoAttribute>() != null)
                    return assembly;
            }
            log.LogError($"bridge.mod_missing mods={payload.ModsDirectory}");
        }
        catch (Exception exception)
        {
            log.LogError($"bridge.mod_load_failed {exception}");
        }
        return null;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        var name = scene.name;
        if (name == MainMenuScene)
            _payload?.ConfirmLaunch();
        _host?.SceneLoaded(BuildIndexOf(scene), name);
    }

    private static void OnSceneUnloaded(Scene scene) =>
        _host?.SceneUnloaded(BuildIndexOf(scene), scene.name);

    private static bool _buildIndexUnavailable;

    // The game's IL2CPP build strips Scene.GetBuildIndexInternal_Injected, so reading buildIndex
    // throws and the melon never heard of the scene. Upstream only logs the index; -1 stands in.
    private static int BuildIndexOf(Scene scene)
    {
        if (_buildIndexUnavailable)
            return -1;
        try
        {
            return scene.buildIndex;
        }
        catch (Exception)
        {
            _buildIndexUnavailable = true;
            return -1;
        }
    }

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
