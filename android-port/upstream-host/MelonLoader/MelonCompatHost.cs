using System.Reflection;
using MelonLoader.Utils;

namespace MelonLoader;

/// <summary>
/// Runs one melon the way MelonLoader would: the bridge creates it from the mod assembly, then
/// forwards Unity's frame, scene and quit callbacks. Every callback is isolated: one that throws is
/// logged and never reaches the caller, so a failing melon cannot take the bridge down.
/// </summary>
public sealed class MelonCompatHost
{
    private readonly MelonMod _melon;
    private bool _initialized;
    private bool _stopped;
    private bool _lateInitialized;

    private MelonCompatHost(MelonMod melon) => _melon = melon;

    public MelonMod Melon => _melon;

    /// <summary>
    /// Sets the MelonLoader base directory, then instantiates the melon the assembly's
    /// <see cref="MelonInfoAttribute"/> names.
    /// </summary>
    public static MelonCompatHost Create(Assembly modAssembly, string melonBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(modAssembly);
        MelonEnvironment.SetBaseDirectory(melonBaseDirectory);
        var info =
            modAssembly.GetCustomAttribute<MelonInfoAttribute>()
            ?? throw new InvalidOperationException(
                $"{modAssembly.GetName().Name} has no [assembly: MelonInfo]."
            );
        if (!typeof(MelonMod).IsAssignableFrom(info.SystemType))
            throw new InvalidOperationException($"{info.SystemType} is not a MelonMod.");
        var melon = (MelonMod)Activator.CreateInstance(info.SystemType)!;
        melon.Info = info;
        melon.LoggerInstance = new MelonLogger.Instance(info.Name);
        melon.HarmonyId = modAssembly.FullName!;
        return new MelonCompatHost(melon);
    }

    public void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        Run(nameof(MelonBase.OnEarlyInitializeMelon), _melon.OnEarlyInitializeMelon);
        Run(nameof(MelonBase.OnInitializeMelon), _melon.OnInitializeMelon);
    }

    public void Update()
    {
        if (!_initialized || _stopped)
            return;
        // MelonLoader runs OnLateInitializeMelon once the first frame is reached.
        if (!_lateInitialized)
        {
            _lateInitialized = true;
            Run(nameof(MelonBase.OnLateInitializeMelon), _melon.OnLateInitializeMelon);
        }
        Run(nameof(MelonBase.OnUpdate), _melon.OnUpdate);
    }

    public void LateUpdate()
    {
        if (_initialized && !_stopped)
            Run(nameof(MelonBase.OnLateUpdate), _melon.OnLateUpdate);
    }

    public void SceneLoaded(int buildIndex, string sceneName)
    {
        if (!_initialized || _stopped)
            return;
        Run(
            nameof(MelonMod.OnSceneWasLoaded),
            () => _melon.OnSceneWasLoaded(buildIndex, sceneName)
        );
        Run(
            nameof(MelonMod.OnSceneWasInitialized),
            () => _melon.OnSceneWasInitialized(buildIndex, sceneName)
        );
    }

    public void SceneUnloaded(int buildIndex, string sceneName)
    {
        if (_initialized && !_stopped)
            Run(
                nameof(MelonMod.OnSceneWasUnloaded),
                () => _melon.OnSceneWasUnloaded(buildIndex, sceneName)
            );
    }

    /// <summary>Application quit, then deinitialize, each once.</summary>
    public void Quit()
    {
        if (!_initialized || _stopped)
            return;
        _stopped = true;
        Run(nameof(MelonBase.OnApplicationQuit), _melon.OnApplicationQuit);
        Run(nameof(MelonBase.OnDeinitializeMelon), _melon.OnDeinitializeMelon);
    }

    private void Run(string callback, Action step)
    {
        try
        {
            step();
        }
        catch (Exception exception)
        {
            _melon.LoggerInstance.Error($"{callback} failed", exception);
        }
    }
}
