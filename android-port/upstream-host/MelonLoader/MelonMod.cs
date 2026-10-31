using HarmonyLib;

namespace MelonLoader;

/// <summary>The callbacks and members a melon sees, as under MelonLoader.</summary>
public abstract class MelonBase
{
    public MelonInfoAttribute Info { get; internal set; } = null!;

    public MelonLogger.Instance LoggerInstance { get; internal set; } = null!;

    private Harmony? _harmony;

    // Created on first use, as nothing else needs HarmonyX loaded: its detour runtime targets
    // the launcher's CoreCLR, not every runtime the compat assembly is tested on.
    public Harmony HarmonyInstance => _harmony ??= new Harmony(HarmonyId);

    internal string HarmonyId { get; set; } = null!;

    public virtual void OnEarlyInitializeMelon() { }

    public virtual void OnInitializeMelon() { }

    public virtual void OnLateInitializeMelon() { }

    public virtual void OnDeinitializeMelon() { }

    public virtual void OnUpdate() { }

    public virtual void OnLateUpdate() { }

    public virtual void OnApplicationQuit() { }
}

/// <summary>A mod melon: adds the scene callbacks.</summary>
public abstract class MelonMod : MelonBase
{
    public virtual void OnSceneWasLoaded(int buildIndex, string sceneName) { }

    public virtual void OnSceneWasInitialized(int buildIndex, string sceneName) { }

    public virtual void OnSceneWasUnloaded(int buildIndex, string sceneName) { }
}
