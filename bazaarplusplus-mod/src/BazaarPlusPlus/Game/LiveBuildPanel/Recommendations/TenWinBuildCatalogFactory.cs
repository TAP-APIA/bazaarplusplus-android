#nullable enable
using System.Reflection;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.RemoteEmbeddedCatalog;
using BazaarPlusPlus.ModApi.Http;

namespace BazaarPlusPlus.Game.LiveBuildPanel.Recommendations;

internal static class TenWinBuildCatalogFactory
{
    internal const string EmbeddedResourceName =
        "BazaarPlusPlus.Data.BuildRecommendations.builds.json";

    // The V6 metrics origin owns the contract, but its run ingest can publish a one-hero corpus
    // while the legacy metrics origin still serves the full one. Ordered: primary first.
    internal static readonly string[] RemoteUrls =
    {
        "https://bpp-metrics-v6.bazaarplusplus.com/builds/latest.json",
        "https://bpp-metrics.bazaarplusplus.com/builds/latest.json",
    };
    private const string CacheFileName = "builds.json";

    // The cache file's write time is not the corpus's age. A corpus whose generated_at is already
    // days old can land on a device late (a re-download after a long offline spell, a clock that was
    // behind when the file was written), and RemoteEmbeddedCatalog measures staleness from that
    // write time alone. At 20 h such a file counted as fresh, so no background refresh ran, no
    // remote event appeared, and the panel served old builds until the file itself aged out.
    // One hour bounds how long one cold start may serve a stale copy. A successful refresh rewrites
    // the file, so later starts inside the hour reuse it instead of re-downloading the ~1 MB payload.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private static readonly HttpClient HttpClient = BppHttpClientFactory.Create(
        productVersion: BppPluginVersion.Current,
        userAgentSuffix: "TenWinBuildRepository",
        // ~1 MB corpus over tablet wifi: the old 10 s budget timed out before the body arrived.
        timeout: TimeSpan.FromSeconds(30)
    );

    internal static IRemoteEmbeddedCatalog<TenWinBuildCorpus> Create(string dataRootPath)
    {
        var cache = new FileCatalogCache(BuildCacheFilePath(dataRootPath));
        return new RemoteEmbeddedCatalog<TenWinBuildCorpus>(
            new TenWinBuildCatalogParser(),
            new AssemblyResourceCatalogSource(
                Assembly.GetExecutingAssembly(),
                EmbeddedResourceName
            ),
            cache,
            CreateRemoteSource(),
            SystemCatalogClock.Instance,
            ThreadPoolCatalogRefreshScheduler.Instance,
            new TenWinBuildCatalogObserver(cache.FilePath),
            CacheDuration
        );
    }

    private static IRemoteCatalogSource CreateRemoteSource()
    {
        var sources = new IRemoteCatalogSource[RemoteUrls.Length];
        for (var index = 0; index < RemoteUrls.Length; index++)
            sources[index] = new HttpRemoteCatalogSource(HttpClient, RemoteUrls[index]);

        return new FallbackRemoteCatalogSource(sources);
    }

    internal static string BuildCacheFilePath(string dataRootPath) =>
        Path.Combine(dataRootPath, CacheFileName);
}

internal sealed class TenWinBuildCatalogParser : ICatalogParser<TenWinBuildCorpus>
{
    public CatalogParseResult<TenWinBuildCorpus> Parse(string document, CatalogSource source)
    {
        var corpus = TenWinBuildCorpus.Parse(document);
        return corpus == null
            ? CatalogParseResult<TenWinBuildCorpus>.Failure("invalid_response")
            : CatalogParseResult<TenWinBuildCorpus>.Success(corpus);
    }
}

internal sealed class TenWinBuildCatalogObserver : IRemoteEmbeddedCatalogObserver<TenWinBuildCorpus>
{
    private readonly object _sync = new();
    private readonly string _cachePath;
    private readonly BuildRecommendationCorpusLogState _logState = new();
    private LiveBuildCorpusSource _currentSource = LiveBuildCorpusSource.Unavailable;
    private int _currentBuildCount;

    internal TenWinBuildCatalogObserver(string cachePath)
    {
        _cachePath = cachePath;
    }

    public void OnWarmStarted() => _logState.ReportWarmupStarted();

    public void OnInitialLoad(CatalogInitialLoadResult<TenWinBuildCorpus> result)
    {
        if (result.Snapshot is { } snapshot)
        {
            var source = MapSource(snapshot.Source);
            lock (_sync)
            {
                _currentSource = source;
                _currentBuildCount = snapshot.Value.BuildCount;
            }

            if (snapshot.Source == CatalogSource.Cache)
            {
                _logState.ReportCacheLoaded(
                    snapshot.Value.BuildCount,
                    snapshot.IsStale,
                    _cachePath
                );
            }

            if (snapshot.Issue is { } issue)
            {
                _logState.ReportDegraded(
                    new CorpusDegradation(
                        MapInitialReason(issue.Kind),
                        source,
                        snapshot.Value.BuildCount,
                        snapshot.IsStale,
                        CachePathFor(issue.Kind),
                        issue.Exception
                    )
                );
            }
            else
            {
                _logState.ReportReady(source, snapshot.Value.BuildCount);
            }
            return;
        }

        var unavailable = result.Issue ?? new CatalogIssue(CatalogIssueKind.Unexpected);
        _logState.ReportDegraded(
            new CorpusDegradation(
                MapInitialReason(unavailable.Kind),
                LiveBuildCorpusSource.Unavailable,
                0,
                Expired: false,
                CachePathFor(unavailable.Kind),
                unavailable.Exception
            )
        );
    }

    public void OnRefreshQueued(CatalogIssue reason) =>
        _logState.ReportRefreshQueued(MapInitialReason(reason.Kind));

    public void OnRefreshCompleted(
        CatalogRefreshTrigger trigger,
        CatalogRefreshResult<TenWinBuildCorpus> result
    )
    {
        if (result.Succeeded && result.Snapshot is { } snapshot)
        {
            lock (_sync)
            {
                _currentSource = LiveBuildCorpusSource.Remote;
                _currentBuildCount = snapshot.Value.BuildCount;
            }

            _logState.ReportRemoteLoaded(snapshot.Value.BuildCount);
            if (snapshot.Issue is { Kind: CatalogIssueKind.CacheWriteFailed } cacheIssue)
                _logState.ReportCacheWriteDegraded(_cachePath, cacheIssue.Exception!);
            else
                _logState.ReportCacheWriteRecovered();

            if (trigger == CatalogRefreshTrigger.Background)
            {
                _logState.ReportRecovered(LiveBuildCorpusSource.Remote, snapshot.Value.BuildCount);
            }
            else
            {
                _logState.ResetDegradedSilently();
            }
            return;
        }

        if (trigger != CatalogRefreshTrigger.Background)
            return;

        LiveBuildCorpusSource source;
        int buildCount;
        lock (_sync)
        {
            source = _currentSource;
            buildCount = _currentBuildCount;
        }
        var issue = result.Issue ?? new CatalogIssue(CatalogIssueKind.Unexpected);
        _logState.ReportDegraded(
            new CorpusDegradation(
                issue.Kind == CatalogIssueKind.RefreshQueueFailed
                    ? LiveBuildCorpusReasonCode.RefreshQueueFailed
                    : LiveBuildCorpusReasonCode.RemoteRefreshFailed,
                source,
                buildCount,
                Expired: false,
                CachePath: null,
                issue.Exception
            )
        );
    }

    private string? CachePathFor(CatalogIssueKind issue) =>
        issue
            is CatalogIssueKind.CacheMissing
                or CatalogIssueKind.CacheStale
                or CatalogIssueKind.CacheReadFailed
                or CatalogIssueKind.CacheInvalid
            ? _cachePath
            : null;

    private static LiveBuildCorpusSource MapSource(CatalogSource source) =>
        source switch
        {
            CatalogSource.Cache => LiveBuildCorpusSource.Cache,
            CatalogSource.Embedded => LiveBuildCorpusSource.Embedded,
            CatalogSource.Remote => LiveBuildCorpusSource.Remote,
            _ => LiveBuildCorpusSource.Unavailable,
        };

    private static LiveBuildCorpusReasonCode MapInitialReason(CatalogIssueKind issue) =>
        issue switch
        {
            CatalogIssueKind.CacheStale => LiveBuildCorpusReasonCode.StaleCache,
            CatalogIssueKind.CacheMissing => LiveBuildCorpusReasonCode.EmbeddedFallback,
            CatalogIssueKind.CacheReadFailed => LiveBuildCorpusReasonCode.CacheReadFailed,
            CatalogIssueKind.CacheInvalid => LiveBuildCorpusReasonCode.CacheInvalid,
            CatalogIssueKind.EmbeddedMissing => LiveBuildCorpusReasonCode.EmbeddedMissing,
            CatalogIssueKind.EmbeddedInvalid or CatalogIssueKind.EmbeddedReadFailed =>
                LiveBuildCorpusReasonCode.EmbeddedInvalid,
            CatalogIssueKind.RefreshQueueFailed => LiveBuildCorpusReasonCode.RefreshQueueFailed,
            _ => LiveBuildCorpusReasonCode.WarmupFailed,
        };
}
