#nullable enable

namespace BazaarPlusPlus.Game.CollectionPanel;

internal enum CollectionPanelLogReasonCode
{
    OverlayHostUnavailable,
    NotMounted,
    UnknownPanel,
    CombatActive,
    ProbeReadFailed,
    PendingBindWaitFailed,
    CardMapTaskFailed,
    CardMapNull,
    StaticDataNotReady,
    TemplateLookupFailed,
    BindException,
    ButtonMissing,
    MissingCollectionButton,
    GearFootprintUnavailable,
    CollectionFootprintUnavailable,
    AnchorCanvasUnavailable,
    PlacementBlocked,
    TargetLocalPositionUnavailable,
    AddressablesLoadException,
    AddressablesLoadFailed,
    InvalidSavedHero,
    UnsupportedHero,
    IdentityUnavailable,
    ResourceMissing,
    ResourceStreamUnavailable,
    SourceCatalogInvalid,
    LocaleChange,
    RuntimeDispose,
    StaticDataManagerChanged,
    TierTooltipMergeException,
    CachedLoadFailed,

    // The silent exits of CollectionItemLoadArtPatch.LoadArtFromCache. Each one draws the card's
    // white face instead of art, and each needs a distinct code so a single device log names the
    // cause: no card data, an unusable art key, a cache miss for the SO, an SO without a material,
    // a material that could not be built, a material whose main texture never loaded, and a card
    // that lost its RawImage slot entirely (neither texture nor material can be shown).
    CardDataMissing,
    ArtKeyMissing,
    ArtAssetMissing,
    CardMaterialMissing,
    MaterialMissing,
    CardTextureMissing,
    CardImageMissing,
    NativePreviewUnavailable,
    NativePreviewRuntimeFailed,
}

internal enum CollectionTierField
{
    Active,
    Passive,
    Cooldown,
}

internal enum CollectionPortraitReasonCode
{
    CollectionManagerUnavailable,
    DefaultSkinUnavailable,
    PortraitUnavailable,
    LoadException,
    ArtKeyUnavailable,
    AssetLoaderUnavailable,
    EncounterAssetUnavailable,
}

internal enum CollectionTypographyReasonCode
{
    IconResolveException,
    ConfigurationMethodUnavailable,
    ConfigurationInvocationException,
}

internal enum CollectionPanelSelectionProbe
{
    RunState,
    Hero,
    Day,
    Encounter,
}

internal enum CollectionPanelLoadPhase
{
    OpenPrologue,
    PanelLoad,
}

internal enum CollectionPanelLoadOutcome
{
    Completed,
    Loaded,
    Unavailable,
}

internal enum CollectionPanelLoadSegment
{
    CatalogAcquire,
    Catalog,
    Filter,
    Refresh,
}

internal enum CollectionCardBindStage
{
    Bind,
}

internal enum CollectionCardDisplayStage
{
    Show,
}

internal enum CollectionGridPerformancePhase
{
    FirstWindowBind,
}

internal enum CollectionCardArtStatus
{
    ArtUnavailable,
}

internal enum CollectionCacheKind
{
    Art,
    Material,
}

internal enum CollectionCacheCleanupStage
{
    Release,
    EvictRelease,
    Destroy,
    EvictDestroy,
}

internal enum CollectionHoverOperation
{
    OnHover,
    OnHoverOut,
}
