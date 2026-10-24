#nullable enable
using BazaarPlusPlus.Infrastructure.RemoteEmbeddedCatalog;

namespace BazaarPlusPlus.Game.LiveBuildPanel.Recommendations;

/// <summary>
/// Resolves the corpus from ordered origins and returns the first document that is dense enough
/// to answer recall. The V6 metrics origin is the contract origin, but while its run ingest is
/// thin it publishes a one-hero corpus, so a sparse document is rejected in favour of the next
/// origin. If every origin is sparse the densest one still wins (thin beats nothing); if every
/// origin fails the primary failure is rethrown so the catalog keeps reporting a remote problem.
/// </summary>
internal sealed class FallbackRemoteCatalogSource : IRemoteCatalogSource
{
    private const int MinimumBuilds = 64;
    private const int MinimumCoveredHeroes = 4;

    private readonly IReadOnlyList<IRemoteCatalogSource> _sources;

    internal FallbackRemoteCatalogSource(IReadOnlyList<IRemoteCatalogSource> sources)
    {
        if (sources == null || sources.Count == 0)
            throw new ArgumentException("At least one remote source is required.", nameof(sources));

        _sources = sources;
    }

    public async ValueTask<string?> DownloadAsync(CancellationToken cancellationToken)
    {
        Exception? firstFailure = null;
        string? sparseDocument = null;
        var sparseBuilds = -1;

        foreach (var source in _sources)
        {
            string? document;
            try
            {
                document = await source.DownloadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
                continue;
            }

            if (string.IsNullOrWhiteSpace(document))
                continue;

            var corpus = TenWinBuildCorpus.Parse(document);
            if (corpus == null)
                continue;

            if (IsDenseEnough(corpus))
                return document;

            if (corpus.BuildCount > sparseBuilds)
            {
                sparseBuilds = corpus.BuildCount;
                sparseDocument = document;
            }
        }

        if (sparseDocument != null)
            return sparseDocument;
        if (firstFailure != null)
            throw firstFailure;
        return null;
    }

    private static bool IsDenseEnough(TenWinBuildCorpus corpus) =>
        corpus.BuildCount >= MinimumBuilds
        && corpus.HeroBuildCounts.Count(row => row.BuildCount > 0) >= MinimumCoveredHeroes;
}
