#pragma warning disable CS0436
#nullable enable
using BazaarPlusPlus.Core.Config;
using BazaarPlusPlus.Core.Runtime;
using BazaarPlusPlus.Game.CollectionPanel.Data;
using BazaarPlusPlus.Game.CombatReplay;
using BazaarPlusPlus.Game.EventPreview;
using BazaarPlusPlus.Game.Input;
using BazaarPlusPlus.Game.LegendaryPosition;
using BazaarPlusPlus.Game.Settings;
using BazaarPlusPlus.Game.Supporters;
using BazaarPlusPlus.Game.Tooltips;
using BazaarPlusPlus.GameInterop;
using BazaarPlusPlus.GameInterop.Fonts;
using BazaarPlusPlus.GameInterop.Localization;
using BazaarPlusPlus.GameInterop.Tooltips;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using BazaarPlusPlus.Localization;
using BazaarPlusPlus.ModApi;
using BazaarPlusPlus.ModApi.Clients;
using BazaarPlusPlus.ModApi.Http;
using BazaarPlusPlus.Patches;
using BazaarPlusPlus.Patches.Tooltips;
using BazaarPlusPlus.Storage.Paths;
using BazaarPlusPlus.Storage.Sqlite;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
#if BPP_ANDROID
using BepInEx.Unity.IL2CPP;
#endif

namespace BazaarPlusPlus;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
#if BPP_ANDROID
public class Plugin : BasePlugin
#else
public class Plugin : BaseUnityPlugin
#endif
{
    private readonly Harmony _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
    private BppComposition? _composition;
    private ModApiSession? _modApiSession;
    private BazaarDbLinkClient? _bazaarDbLinkClient;
    private bool _patchesApplied;
    private bool _teardownStarted;

    // Captured at startup because teardown releases the database after the
    // composition that owns the path provider is already disposed.
    private string? _runLogDatabasePath;

#if BPP_ANDROID
    private GameObject? _host;

    static Plugin()
    {
        try
        {
            // Preload the managed NuGet Newtonsoft.Json.dll from BepInEx/plugins before any method
            // referencing Newtonsoft.Json is JIT-compiled. Otherwise BepInEx's interop resolver
            // loads BepInEx/interop/Newtonsoft.Json.dll (the IL2CPP proxy), which passes null
            // Il2CppType* pointers to il2cpp_class_from_il2cpp_type when deserialising managed DTOs.
            var managedNewtonsoft = System.IO.Path.Combine(
                BepInEx.Paths.PluginPath,
                "Newtonsoft.Json.dll"
            );
            if (System.IO.File.Exists(managedNewtonsoft))
            {
                var asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(
                    managedNewtonsoft
                );
                System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
                    string.Equals(name.Name, "Newtonsoft.Json", StringComparison.OrdinalIgnoreCase)
                        ? asm
                        : null;
                AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
                    args.Name.StartsWith("Newtonsoft.Json,", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        args.Name,
                        "Newtonsoft.Json",
                        StringComparison.OrdinalIgnoreCase
                    )
                        ? asm
                        : null;
                BppStartupTrace.Write("plugin:newtonsoft-preloaded " + asm.FullName);
            }
        }
        catch (Exception ex)
        {
            BppStartupTrace.Write("plugin:newtonsoft-preload-failed " + ex.Message);
        }
    }

    internal GameObject HostObject => _host!;

    // BasePlugin has no gameObject; the Android entry point owns one explicitly.
    private GameObject Host => _host!;

    public override void Load()
    {
        BppLog.Install(Log);
        AndroidNativeLibraries.Initialize();
        AndroidTlsCertificateBridge.Initialize();
        RegisterManagedComponents();
        _host = BppGameObjectFactory.Create("BazaarPlusPlus");
        UnityEngine.Object.DontDestroyOnLoad(_host);
        BppCoroutines.InitializeMainThread();
#else
    private GameObject Host => gameObject;

    protected virtual void Awake()
    {
        BppLog.Install(Logger);
#endif
        NativeGameTypography.InitializeForCurrentThread();
        var phase = PluginInitializationPhase.PluginVersion;
        try
        {
#if BPP_ANDROID
            BppPluginVersion.Initialize(typeof(Plugin).Assembly.Location);
#else
            BppPluginVersion.Initialize(Info.Location);
#endif

            var configFile = CreatePluginConfigFile();

            phase = PluginInitializationPhase.Composition;
            var gameBuild = GameBuildInfoResolver.Resolve();
            _composition = new BppComposition(configFile, gameBuild);

            var services = _composition.Services;
            var dataRoot = services.Paths.DataRootDirectoryPath;
            _runLogDatabasePath = string.IsNullOrWhiteSpace(dataRoot)
                ? null
                : PathConstants.RunLogDatabase(dataRoot!);
            BppPatchHost.Install(services, _composition.PatchFeatures);

            if (gameBuild.DetectionWarning != null)
            {
                BppLog.WarnEvent(
                    new BppLogEvent(
                        BppLogFeatureScope.Plugin,
                        "plugin.game_build.degraded",
                        storm: ["reason_code"]
                    ),
                    ("game_build", gameBuild.RawVersion),
                    ("build_channel", gameBuild.Channel),
                    (
                        "reason_code",
                        string.IsNullOrWhiteSpace(gameBuild.RawVersion)
                            ? PluginLogReasonCode.VersionUnreadable
                            : PluginLogReasonCode.DetectionSignalsDisagree
                    )
                );
            }

            phase = PluginInitializationPhase.StaticUtilities;
            InstallStaticUtilities(services, _composition.SettingsDockRegistry);

            phase = PluginInitializationPhase.HarmonyPatches;
            ApplyHarmonyPatches();
            NativeTooltipSuppression.CapturePatchCapabilities();

            phase = PluginInitializationPhase.ReplayRuntime;
            // CombatReplayRuntime is constructed before composition.Start() because RunLifecycle
            // and several features take a reference through CombatReplayModule. Not a mountable.
            var combatReplayRuntime = Host.AddComponent<CombatReplayRuntime>();
            combatReplayRuntime.Initialize(
                services,
                _composition.RunLifecycle,
                _composition.PvpBattleCatalog,
                () => Host.GetComponent<Game.CombatReplay.Video.CombatReplayVideoRecorder>()
            );
            _composition.AttachCombatReplayRuntime(combatReplayRuntime);

            phase = PluginInitializationPhase.Features;
            _composition.Start();

            phase = PluginInitializationPhase.OnlineServices;
#if BPP_ANDROID
            // Keep History's online session and account-link client disabled until Android
            // transport is verified. Earlier aborts were observed near this initialization
            // phase, but do not establish HttpClient construction as their cause. Other
            // features own independent HTTP clients; this is not a global network gate.
#else
            BuildOnlineServices();
#endif
            _composition.AttachModApiSession(_modApiSession);
            _composition.AttachAccountLinkClient(_bazaarDbLinkClient);

            phase = PluginInitializationPhase.Mountables;
            _composition.Mountables.MountAll(Host, services);
#if BPP_ANDROID
            // The panels are hotkey-driven; on a touch device nothing would be reachable without
            // this always-on entry button.
            Host.AddComponent<BppTouchEntry>();
#endif
            BppStartupTrace.Write("plugin:after-mount");
            BppLog.InfoEvent(
                new BppLogEvent(BppLogFeatureScope.Plugin, "plugin.initialization.succeeded"),
                ("plugin_version", MyPluginInfo.PLUGIN_VERSION),
                ("game_build", gameBuild.RawVersion),
                ("build_channel", gameBuild.Channel)
            );
            BppStartupTrace.Write("plugin:succeeded-logged");
        }
        catch (Exception ex)
        {
            BppStartupTrace.Write("plugin:init-failed " + ex.GetType().Name);
            BppLog.ErrorEvent(
                new BppLogEvent(BppLogFeatureScope.Plugin, "plugin.initialization.failed"),
                ex,
                ("phase", phase),
                ("reason_code", PluginLogReasonCode.InitializationException)
            );
            CleanupFailedInitialization();
            throw;
        }
    }

    protected virtual void OnDestroy()
    {
        Teardown();
    }

    private void Teardown()
    {
        if (_teardownStarted)
            return;
        _teardownStarted = true;

        var failures = new PluginTeardownAccumulator();
        RunTeardownSteps(failures);
        failures.Run(PluginTeardownStep.ResetPatchHost, BppPatchHost.Reset);
        if (failures.FailedStepCount > 0)
        {
            BppLog.WarnEvent(
                new BppLogEvent(
                    BppLogFeatureScope.Plugin,
                    "plugin.shutdown.degraded",
                    storm: ["reason_code"]
                ),
                failures.FirstException!,
                ("failed_step_count", failures.FailedStepCount),
                ("first_failed_step", failures.FirstFailedStep),
                ("reason_code", PluginLogReasonCode.TeardownStepFailed)
            );
        }
        BppLog.Flush();
    }

    // Every step runs in isolation and Harmony is unpatched first: a throw in any single
    // step must never strand the "patches applied but static utilities uninstalled"
    // zombie state (patched game code rebuilding UI from reset catalogs).
    private void RunTeardownSteps(PluginTeardownAccumulator failures)
    {
        failures.Run(PluginTeardownStep.UnpatchHarmony, UnpatchHarmony);
        failures.Run(
            PluginTeardownStep.UnmountComponents,
            () => _composition?.Mountables.UnmountAll(Host)
        );
        failures.Run(
            PluginTeardownStep.DisposeComposition,
            () =>
            {
                _composition?.Dispose();
                _composition = null;
            }
        );
        failures.Run(
            PluginTeardownStep.DestroyCombatReplayRuntime,
            DestroyComponentIfPresent<CombatReplayRuntime>
        );
        failures.Run(PluginTeardownStep.DisposeOnlineServices, DisposeOnlineServices);
        // After every writer is gone, so the checkpoint can reclaim the whole WAL.
        failures.Run(
            PluginTeardownStep.ReleaseRunLogDatabase,
            () => SqliteShutdown.ReleaseRunLogDatabase(_runLogDatabasePath)
        );
        failures.Run(PluginTeardownStep.UninstallStaticUtilities, UninstallStaticUtilities);
    }

    private ConfigFile CreatePluginConfigFile()
    {
        return new ConfigFile(Path.Combine(Paths.ConfigPath, "BazaarPlusPlus.cfg"), true);
    }

    private static void InstallStaticUtilities(
        IBppServices services,
        SettingsDockEntryRegistry settingsDockRegistry
    )
    {
        LegendaryPositionDisplayFormatter.Install(services.Config);
        var config = services.Config;
        L.Install(
            () => ResolveUiLanguageCode(config),
            () => ChineseScriptConverter.NormalizeMode(config.ChineseLocaleModeConfig.Value)
        );
        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.Plugin, "plugin.localization.language"),
            ("configured", config.UiLanguageConfig.Value.ToString()),
            ("game_language", ReadGameLanguageCode()),
            ("system_language", Application.systemLanguage.ToString()),
            ("resolved", ResolveUiLanguageCode(config))
        );
        BppTouchEntry.Install(
            new Vector2(config.TouchDockAnchorXConfig.Value, config.TouchDockAnchorYConfig.Value),
            anchor =>
            {
                config.TouchDockAnchorXConfig.Value = Mathf.Clamp01(anchor.x);
                config.TouchDockAnchorYConfig.Value = Mathf.Clamp01(anchor.y);
            }
        );
        var attributeUnitLocalizer = BppTooltipText.TryLocalizeKeyword;
        CollectionLocalizationResolver.AttributeUnitLocalizer = attributeUnitLocalizer;
        EventPreviewLocalization.AttributeUnitLocalizer = attributeUnitLocalizer;
        BppSettingsDockCatalog.Install(services.Config, settingsDockRegistry);
        BPPSupporterCatalog.Install(services.Config);
        BppHotkeyService.Install(services.Config);
    }

    private static string ReadGameLanguageCode()
    {
        try
        {
            return PlayerPreferences.Data.LanguageCode ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // Auto follows the game so the mod's own text matches the tooltips it sits next to. Android
    // builds that never persist PreferencesData store no code at all and would otherwise fall back
    // to English, so the system language is the second choice and an explicit override wins.
    private static string ResolveUiLanguageCode(BppConfig config)
    {
        switch (config.UiLanguageConfig.Value)
        {
            case BppUiLanguage.English:
                return "en";
            case BppUiLanguage.Chinese:
                return "zh-Hans";
            case BppUiLanguage.German:
                return "de";
            case BppUiLanguage.Portuguese:
                return "pt-BR";
            case BppUiLanguage.Korean:
                return "ko";
            case BppUiLanguage.Italian:
                return "it";
        }

        var game = ReadGameLanguageCode();
        if (!string.IsNullOrWhiteSpace(game))
            return game;

        return Application.systemLanguage switch
        {
            SystemLanguage.ChineseSimplified => "zh-Hans",
            SystemLanguage.ChineseTraditional => "zh-Hant",
            SystemLanguage.Chinese => "zh",
            SystemLanguage.German => "de",
            SystemLanguage.Portuguese => "pt-BR",
            SystemLanguage.Korean => "ko",
            SystemLanguage.Italian => "it",
            _ => "en",
        };
    }

    private static void UninstallStaticUtilities()
    {
        LegendaryPositionDisplayFormatter.Reset();
        BppTouchEntry.Reset();
        L.Reset();
        CollectionLocalizationResolver.AttributeUnitLocalizer = null;
        EventPreviewLocalization.AttributeUnitLocalizer = null;
        BppSettingsDockCatalog.Reset();
        NativeSettingsLogState.Reset();
        BPPSupporterCatalog.Reset();
        BppHotkeyService.Reset();
        TooltipEncounterProbeReader.Reset();
        BppTooltipSectionRenderPatch.ResetEncounterHealth();
        ChineseTranslationCatalog.Reset();
        NativeGameTypography.Reset();
    }

    private void BuildOnlineServices()
    {
        // BazaarDB account linking targets a different host (bazaardb.gg) and is independent of the
        // mod API base URL, so build it regardless of mod-api routes validity. Its dedicated bare
        // HttpClient carries no mod-api auth/base address; a 30s timeout keeps a hung redeem from
        // stalling the link card for the HttpClient default of ~100s.
        var linkHttpClient = BppHttpClientFactory.Create(
            productVersion: BppPluginVersion.Current,
            userAgentSuffix: "BazaarDbLink",
            timeout: TimeSpan.FromSeconds(30)
        );
        _bazaarDbLinkClient = new BazaarDbLinkClient(
            linkHttpClient,
            new Uri(BazaarDbLinkClient.DefaultRedeemEndpoint)
        );

        // The base URL is a constant, so a null session is a build defect: fail startup.
        _modApiSession =
            ModApiSession.TryCreate(
                ModApiUploadDefaults.ApiBaseUrl,
                BppPluginVersion.Current,
                "OnlineClient",
                TimeSpan.FromSeconds(ModApiUploadDefaults.RequestTimeoutSeconds)
            )
            ?? throw new InvalidOperationException(
                $"Mod API base URL '{ModApiUploadDefaults.ApiBaseUrl}' is not an absolute URL."
            );
    }

    /// <summary>
    /// Injects every managed MonoBehaviour in this assembly into the IL2CPP domain before any
    /// <c>AddComponent</c> runs.
    ///
    /// A managed component that was never injected resolves to a null Il2Cpp class and the runtime
    /// aborts the whole process the moment it is added, which reads as a startup crash rather than a
    /// missing feature.
    /// </summary>
    private static void RegisterManagedComponents()
    {
#if BPP_ANDROID
        Type[] types;
        try
        {
            types = typeof(Plugin).Assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException partial)
        {
            types = Array.FindAll(partial.Types, type => type != null)!;
        }

        foreach (var type in types)
        {
            if (type == null)
                continue;

            try
            {
                if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition)
                    continue;
                if (!typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(type))
                    continue;
            }
            catch (Exception)
            {
                continue;
            }

            try
            {
                Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp(type);
            }
            catch (Exception)
            {
                // Already registered, or a type the injector cannot represent.
            }
        }
#endif
    }

    private void ApplyHarmonyPatches()
    {
        // Patch classes are applied one by one instead of PatchAll(): PatchAll aborts at
        // the first failing class, leaving earlier classes applied and later ones not —
        // one broken game target (e.g. after a game update or on the PTR branch) must
        // degrade only its own feature, never the whole plugin. The flag is set before
        // the loop so a partial application is always unpatched during teardown.
        _patchesApplied = true;
#if BPP_ANDROID
        BppLog.InfoEvent(
            new BppLogEvent(BppLogFeatureScope.Plugin, "plugin.android.patch_capabilities"),
            (
                "unsupported_features",
                "voice_callbacks,collection_variant_controls,upgrade_value_tuples,multi_tier_passive_tooltips,package_merchant_summary"
            )
        );
#endif
        var failedClasses = 0;
        // GetTypesFromAssembly (not Assembly.GetTypes) tolerates types that fail to load;
        // CreateClassProcessor(type).Patch() is a no-op for non-patch types, so this
        // covers exactly PatchAll's discovery set.
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
        {
            try
            {
                _harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception ex)
            {
                failedClasses++;
                BppLog.DebugEvent(
                    new BppLogEvent(BppLogFeatureScope.Plugin, "plugin.patch.apply_failed"),
                    ex,
                    () =>
                        [
                            ("patch_type", type.FullName ?? type.Name),
                            ("reason_code", PluginLogReasonCode.PatchClassException),
                        ]
                );
            }
        }

        if (failedClasses > 0)
            BppLog.WarnEvent(
                new BppLogEvent(
                    BppLogFeatureScope.Plugin,
                    "plugin.patches.degraded",
                    storm: ["reason_code"]
                ),
                ("failed_patch_count", failedClasses),
                ("reason_code", PluginLogReasonCode.PatchClassesFailed)
            );
    }

    private void CleanupFailedInitialization()
    {
        Teardown();
    }

    private void DisposeOnlineServices()
    {
        _bazaarDbLinkClient?.Dispose();
        _bazaarDbLinkClient = null;
        _modApiSession?.Dispose();
        _modApiSession = null;
    }

    private void UnpatchHarmony()
    {
        if (!_patchesApplied)
            return;

        _harmony.UnpatchSelf();
        NativeTooltipSuppression.ClearPatchCapabilities();
        _patchesApplied = false;
    }

    private void DestroyComponentIfPresent<T>()
        where T : Component
    {
        var component = Host.GetComponent<T>();
        if (component != null)
            UnityEngine.Object.DestroyImmediate(component);
    }
}
