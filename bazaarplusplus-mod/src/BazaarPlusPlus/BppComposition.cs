#nullable enable
using BazaarPlusPlus.Core.Config;
using BazaarPlusPlus.Core.Events;
using BazaarPlusPlus.Core.Paths;
using BazaarPlusPlus.Core.Runtime;
using BazaarPlusPlus.Game.BilingualItemNames;
using BazaarPlusPlus.Game.BundlePipeline;
using BazaarPlusPlus.Game.CollectionPanel;
using BazaarPlusPlus.Game.CombatReplay;
using BazaarPlusPlus.Game.CombatReplay.Video;
using BazaarPlusPlus.Game.CombatStatusBar;
using BazaarPlusPlus.Game.CosmeticNames;
using BazaarPlusPlus.Game.EventPreview;
using BazaarPlusPlus.Game.GraphicsUpscaling;
using BazaarPlusPlus.Game.HistoryPanel;
using BazaarPlusPlus.Game.Input;
using BazaarPlusPlus.Game.ItemEnchantPreview;
using BazaarPlusPlus.Game.LegendaryPosition;
using BazaarPlusPlus.Game.LiveBuildPanel;
using BazaarPlusPlus.Game.LiveBuildPanel.Recommendations;
using BazaarPlusPlus.Game.Lobby;
using BazaarPlusPlus.Game.MusicNotes;
using BazaarPlusPlus.Game.NameOverride;
using BazaarPlusPlus.Game.OverlayPanels;
#if !BPP_ANDROID
using BazaarPlusPlus.Game.PostCombatImpact;
#endif
using BazaarPlusPlus.Game.PvpBattles.Persistence;
using BazaarPlusPlus.Game.QuestPreview;
using BazaarPlusPlus.Game.RunLifecycle;
using BazaarPlusPlus.Game.RunLogging;
using BazaarPlusPlus.Game.Screenshots;
using BazaarPlusPlus.Game.Settings;
using BazaarPlusPlus.Game.Supporters;
using BazaarPlusPlus.Game.Tooltips;
using BazaarPlusPlus.Game.VoiceSubtitles;
using BazaarPlusPlus.GameInterop;
using BazaarPlusPlus.GameInterop.CardPreview;
using BazaarPlusPlus.GameInterop.DayTiers;
using BazaarPlusPlus.GameInterop.Encounter;
using BazaarPlusPlus.GameInterop.RunSnapshot;
using BazaarPlusPlus.GameInterop.StaticCards;
using BazaarPlusPlus.GameInterop.Tooltips;
using BazaarPlusPlus.GameInterop.VoiceSubtitles;
using BazaarPlusPlus.Infrastructure.RemoteEmbeddedCatalog;
using BazaarPlusPlus.ModApi.Clients;
using BazaarPlusPlus.Patches;
#if !BPP_ANDROID
using BazaarPlusPlus.Patches.PostCombatImpact;
#endif
using BazaarPlusPlus.Patches.Tooltips;
using BazaarPlusPlus.Storage.Paths;
using BepInEx.Configuration;

namespace BazaarPlusPlus;

internal sealed class BppComposition : IDisposable
{
    private readonly InMemoryBppEventBus _eventBus = new();
    private readonly BppConfig _config;
    private readonly BepInExPathProvider _paths = new();
    private readonly RunContextStore _runContext = new();
    private readonly GameStateProbe _gameStateProbe = new();
    private readonly EncounterStateProbe _encounterStateProbe = new();
    private readonly RunSnapshotProbe _runSnapshotProbe = new();
    private readonly BppStaticCardMapProvider _staticCardMapProvider = new();
    private readonly IGameDataDayTierResolver _dayTierResolver = new GameDataDayTierResolver(
        new GameDataDayTierSource()
    );
    private readonly BppRuntimeServices _services;
    private readonly BppFeatureRegistry _featureRegistry = new();
    private readonly BppMountableRegistry _mountables = new();
    private readonly SettingsDockEntryRegistry _settingsDockRegistry = new();
    private readonly RunLifecycleModule _runLifecycle;
    private readonly CombatReplayModule _combatReplayModule;
    private readonly CombatStatusBarModule _combatStatusBarModule;
#if !BPP_ANDROID
    private readonly PostCombatImpactModule _postCombatImpactModule;
#endif
    private readonly RunLoggingModule _runLoggingModule;
    private readonly BundleSealCoordinator _bundleSealCoordinator;
    private readonly EncounterPreviewModule _encounterPreviewModule;
    private readonly INativeCardPreviewHost _nativeCardPreviewHost;
    private readonly EndOfRunCaptureWorkflow _endOfRunCaptureWorkflow;
    private readonly BppPatchFeatures _patchFeatures;
    private readonly VoiceSubtitlesModule _voiceSubtitlesModule;
    private readonly VoiceSubtitlesInteropModule _voiceSubtitlesInteropModule;
    private readonly SupporterCatalogModule _supporterCatalogModule;
    private readonly IRemoteEmbeddedCatalog<TenWinBuildCorpus> _buildRecommendationCatalog;
    private readonly BuildRecommendationRepository _buildRecommendationRepository;
    private ModApiSession? _modApiSessionRef;
    private BazaarDbLinkClient? _accountLinkClientRef;
    private PvpBattleSqliteStore? _pvpBattleCatalog;

    public IBppServices Services => _services;
    public RunLifecycleModule RunLifecycle => _runLifecycle;

    public IPvpBattleCatalog PvpBattleCatalog =>
        _pvpBattleCatalog ??= new PvpBattleSqliteStore(
            PathConstants.RunLogDatabase(_paths.RequireDataRoot())
        );

    public BppMountableRegistry Mountables => _mountables;
    public SettingsDockEntryRegistry SettingsDockRegistry => _settingsDockRegistry;
    public BppPatchFeatures PatchFeatures => _patchFeatures;

    public BppComposition(ConfigFile configFile, IGameBuildInfo gameBuild)
    {
        if (configFile == null)
            throw new ArgumentNullException(nameof(configFile));
        if (gameBuild == null)
            throw new ArgumentNullException(nameof(gameBuild));

        _config = new BppConfig(configFile);
        _paths.Initialize();

        _services = new BppRuntimeServices(
            _eventBus,
            _config,
            _paths,
            _runContext,
            _gameStateProbe,
            _encounterStateProbe,
            _runSnapshotProbe,
            gameBuild
        );

        _runLifecycle = new RunLifecycleModule(_eventBus, _gameStateProbe, _runContext);
        _combatReplayModule = new CombatReplayModule(_eventBus);
        _combatStatusBarModule = new CombatStatusBarModule(_eventBus);
#if !BPP_ANDROID
        _postCombatImpactModule = new PostCombatImpactModule(_eventBus, _staticCardMapProvider);
#endif
        _voiceSubtitlesModule = new VoiceSubtitlesModule(_paths.RequireDataRoot());
        _voiceSubtitlesInteropModule = new VoiceSubtitlesInteropModule();
        _supporterCatalogModule = new SupporterCatalogModule(
            () => _config.UseFixedSupporterListConfig.Value,
            _paths.RequireDataRoot()
        );
        _nativeCardPreviewHost = new NativeCardPreviewHost(new NativeTooltipDataFactoryAdapter());
        _buildRecommendationCatalog = TenWinBuildCatalogFactory.Create(_paths.RequireDataRoot());
        _buildRecommendationRepository = new BuildRecommendationRepository(
            _buildRecommendationCatalog
        );
        var encounterPreviewCachePath = System.IO.Path.Combine(
            _paths.RequireDataRoot(),
            "EncounterPreview",
            "preview-plans.json"
        );
        _encounterPreviewModule = new EncounterPreviewModule(
            _services,
            _staticCardMapProvider,
            _dayTierResolver,
            encounterPreviewCachePath
        );
        _endOfRunCaptureWorkflow = new EndOfRunCaptureWorkflow(_services);
#if BPP_ANDROID
        _patchFeatures = new BppPatchFeatures(_encounterPreviewModule, _endOfRunCaptureWorkflow);
#else
        _patchFeatures = new BppPatchFeatures(
            _encounterPreviewModule,
            _endOfRunCaptureWorkflow,
            _postCombatImpactModule
        );
#endif
        _runLoggingModule = new RunLoggingModule(
            _services,
            PvpBattleCatalog,
            () => _combatReplayModule.Runtime?.HasPendingPersistence == true
        );
        _bundleSealCoordinator = new BundleSealCoordinator(_services);

        _featureRegistry.Register(_runLifecycle);
        _featureRegistry.Register(_combatReplayModule);
        _featureRegistry.Register(_combatStatusBarModule);
#if !BPP_ANDROID
        _featureRegistry.Register(_postCombatImpactModule);
#endif
        _featureRegistry.Register(_voiceSubtitlesInteropModule);
        _featureRegistry.Register(_voiceSubtitlesModule);
        _featureRegistry.Register(_supporterCatalogModule);
        _featureRegistry.Register(_runLoggingModule);
        _featureRegistry.Register(_bundleSealCoordinator);

        _settingsDockRegistry.Register(BazaarDbBundleSettingsDockEntry.Create());
        _settingsDockRegistry.Register(FixedSupporterListSettingsDockEntry.Create());
        VoiceSubtitlesSettingsDockEntry.RegisterAll(_settingsDockRegistry);
        _settingsDockRegistry.Register(ChineseLocaleModeSettingsDockEntry.Create(_eventBus));
        _settingsDockRegistry.Register(BilingualItemNamesSettingsDockEntry.Create());
        _settingsDockRegistry.Register(CosmeticNamesSettingsDockEntry.Create());
        _settingsDockRegistry.Register(CombatStatusBarSettingsDockEntry.Create());
        _settingsDockRegistry.Register(new EndOfRunScreenshotSettingsDockEntry());
        _settingsDockRegistry.Register(new HistoryPanelSettingsDockEntry());
        _settingsDockRegistry.Register(ItemEnchantPreviewSettingsDockEntry.Create());
        _settingsDockRegistry.Register(UpgradePreviewActivationSettingsDockEntry.Create());
        _settingsDockRegistry.Register(EventPreviewSettingsDockEntry.Create());
        _settingsDockRegistry.Register(
            QuestPreviewSettingsDockEntry.Create(() =>
            {
                QuestRewardPreviewTooltipPatch.ClearPooledPresentation();
                AggregateItemMissingTypesTooltipPatch.ClearPooledPresentation();
            })
        );
        _settingsDockRegistry.Register(LegendaryPositionSettingsDockEntry.Create());
        _settingsDockRegistry.Register(NameOverrideSettingsDockEntry.Create());
        if (
            UnityEngine.Application.platform
            is UnityEngine.RuntimePlatform.OSXPlayer
                or UnityEngine.RuntimePlatform.WindowsPlayer
        )
            GraphicsUpscalingSettingsDockEntry.RegisterAll(_settingsDockRegistry);

#if !BPP_ANDROID
        _mountables.Register(new UploadPumpMount());
#endif
        if (
            UnityEngine.Application.platform
            is UnityEngine.RuntimePlatform.OSXPlayer
                or UnityEngine.RuntimePlatform.WindowsPlayer
        )
        {
            _mountables.Register(
                new ComponentMount<GraphicsUpscalingController>(
                    (controller, services) => controller.Initialize(services.Config)
                )
            );
        }
#if !BPP_ANDROID
        // Mounting this observer aborts the process on the Android port: its first Update calls
        // into the interop layer before the projections are ready.
        _mountables.Register(
            new ComponentMount<EventPreviewStaticDataObserver>(
                (observer, _) => observer.Initialize(_encounterPreviewModule)
            )
        );
#endif
        // The overlay host must mount before every Main Overlay Panel mount below: panels
        // register their lifecycle with it through the accessor.
        var overlayPanelHostMount = new OverlayPanelHostMount();
        _mountables.Register(overlayPanelHostMount);
        _mountables.Register(
            new CollectionPanelMount(
                () => overlayPanelHostMount.Host,
                _staticCardMapProvider,
                _nativeCardPreviewHost,
                _dayTierResolver,
                _patchFeatures.CollectionInspection
            )
        );
        _mountables.Register(
            new ComponentMount<CombatReplayVideoRecorder>((c, s) => c.Initialize(s))
        );
        _mountables.Register(new ComponentMount<CombatStatusBar>((c, s) => c.Initialize(s)));
        _mountables.Register(new ComponentMount<NativeMusicNotePreviewController>());
#if !BPP_ANDROID
        // Plugin-lifetime, like _nativeCardPreviewHost: composition does not dispose GameInterop
        // hosts, and the releasable unit is the session the view acquires from it.
        var pairedTooltipHost = new NativePairedTooltipHost();
        _mountables.Register(
            new ComponentMount<PostCombatImpactController>(
                (c, _) =>
                    c.Initialize(
                        _postCombatImpactModule,
                        new NativePostCombatImpactTooltipView(
                            _nativeCardPreviewHost,
                            pairedTooltipHost
                        )
                    )
            )
        );
#endif
        _mountables.Register(
            new ComponentMount<EndOfRunCaptureDriver>(
                (driver, services) => driver.Initialize(_endOfRunCaptureWorkflow, services)
            )
        );
#if !BPP_ANDROID
        _mountables.Register(
            new ComponentMount<MainMenuVersionCheckController>((c, _) => c.Initialize())
        );
#endif
        _mountables.Register(
            new HistoryPanelMount(
                combatReplayRuntime: () => _combatReplayModule.Runtime,
                modApiSession: () => _modApiSessionRef,
                accountLinkClient: () => _accountLinkClientRef,
                overlayHost: () => overlayPanelHostMount.Host
            )
        );
        _mountables.Register(
            new LiveBuildPanelMount(
                () => overlayPanelHostMount.Host,
                _buildRecommendationRepository
            )
        );
        _mountables.Register(new ComponentMount<VoiceLineDisplayDispatcher>());
        _mountables.Register(new ComponentMount<VersionLabelScanner>());
#if !BPP_ANDROID
        _mountables.Register(
            new ComponentMount<TooltipModifierRefreshController>(
                (c, s) => c.Initialize(s.Config, s.EncounterState, _nativeCardPreviewHost)
            )
        );
#endif
    }

    public void AttachCombatReplayRuntime(CombatReplayRuntime runtime) =>
        _combatReplayModule.AttachRuntime(runtime);

    public void AttachModApiSession(ModApiSession? session) => _modApiSessionRef = session;

    public void AttachAccountLinkClient(BazaarDbLinkClient? client) =>
        _accountLinkClientRef = client;

    public void Start() => _featureRegistry.Start();

    public void Dispose()
    {
        _featureRegistry.Stop();
        _endOfRunCaptureWorkflow.Dispose();
        _encounterPreviewModule.Dispose();
        _buildRecommendationCatalog.Dispose();
    }
}
