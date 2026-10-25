#nullable enable
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using TheBazaar.Assets.Scripts.ScriptableObjectsScripts;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace BazaarPlusPlus.Game.CollectionPanel.Grid;

// L2 of the design's four-layer cache stack (§7). Item art (CardAssetDataSO) is loaded by
// CardPreviewItem.LoadArt directly through Addressables.LoadAssetAsync<>, bypassing the
// game's shared AssetLoader cache; each call increments the Addressables ref count and the
// game never releases. Without L2, scrolling through 1146 Items would leak 1146 ref counts.
//
// We hold the AsyncOperationHandle ourselves so we can call Addressables.Release exactly
// once per artKey when the panel closes. An LRU bound caps resident SOs at a fixed number
// regardless of catalog size; the LRU only evicts entries with refcount 0 so handles still
// in use by active or pooled cards stay resident.
//
// A failed artKey is suppressed for a bounded window rather than forever. The panel stays open
// for hours and the grid re-binds cells on every scroll, so a permanent blacklist turns one
// transient failure (catalog still warming, a load cancelled by a panel close, memory pressure
// during a fast scroll) into a blank card for the rest of the process. Retrying is cheap: a
// broken key fails fast and allocates no entry, and the degraded report is storm-suppressed per
// reason_code, so retries cost one Addressables request per key per window and no log flood.
internal sealed class CollectionCardArtCache
{
    private const int DefaultCapacity = 256;

    // Chosen over per-attempt backoff: an art load either succeeds once its dependency is warm
    // (seconds) or the key is genuinely absent, in which case one retry per minute per key stays
    // negligible. Long enough that a fast scroll across many broken keys does not re-request them
    // on every pass, short enough that a transient failure heals without reopening the panel.
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(60);

    private readonly int _capacity;
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodeMap = new(
        StringComparer.Ordinal
    );

    // artKey -> the instant after which that key may be loaded again. UtcNow, not Now, so a device
    // time-zone change cannot move the window, and not a frame counter so retries keep working
    // while the panel stays open. This window does NOT span panel open/close cycles: every Open()
    // builds a new cache and DisposeAll() clears this map, so cross-session behaviour is reset by
    // construction. ClearFailedKeys() is the only in-session escape hatch.
    private readonly Dictionary<string, DateTime> _failedKeys = new(StringComparer.Ordinal);

    public CollectionCardArtCache(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(8, capacity);
    }

    // Returns null on load failure; the caller proceeds without a material assignment, which
    // matches the game's own behaviour for invalid art keys (blank face, frame still drawn).
    public Task<CardAssetDataSO?> Get(string artKey) => GetCore(artKey, acquireRef: false);

    public Task<CardAssetDataSO?> Acquire(string artKey) => GetCore(artKey, acquireRef: true);

    internal void ReportDegraded(
        CollectionPanelLogReasonCode reasonCode,
        string? artKey,
        Exception? exception
    ) => ReportArtDegraded(reasonCode, artKey, exception);

    // Escape hatch for callers that learn the failure cause is gone (catalog refreshed, locale
    // changed); the TTL covers the passive case. CollectionPanel.InvalidateCatalog calls this so
    // the cards bound after an invalidation retry immediately instead of staying blank for up to
    // FailureRetryDelay. Not called per frame: invalidation runs only on locale change or dispose.
    public void ClearFailedKeys() => _failedKeys.Clear();

    public bool ClearFailedKey(string artKey)
    {
        if (string.IsNullOrEmpty(artKey))
            return false;
        return _failedKeys.Remove(artKey);
    }

    // The storm key is the reason code, so a scroll through many broken art keys writes one line
    // per reason per storm window plus a suppressed count.
    private static void ReportArtDegraded(
        CollectionPanelLogReasonCode reasonCode,
        string? artKey,
        Exception? exception
    )
    {
        var degraded = new BppLogEvent(
            BppLogFeatureScope.CollectionPanel,
            "collection_panel.card_art.degraded",
            storm: ["reason_code"]
        );
        var fields = new BppLogField[]
        {
            ("reason_code", reasonCode),
            ("status", CollectionCardArtStatus.ArtUnavailable),
            ("art_key", artKey),
        };
        if (exception == null)
            BppLog.WarnEvent(degraded, fields);
        else
            BppLog.WarnEvent(degraded, exception, fields);
    }

    private bool IsFailureSuppressed(string artKey)
    {
        if (!_failedKeys.TryGetValue(artKey, out var retryAt))
            return false;
        if (DateTime.UtcNow < retryAt)
            return true;
        // Expired: drop the record so the next load is a real attempt and a fresh failure starts a
        // new window, instead of the stale timestamp suppressing every attempt from here on.
        _failedKeys.Remove(artKey);
        return false;
    }

    private void MarkFailed(string artKey) =>
        _failedKeys[artKey] = DateTime.UtcNow + FailureRetryDelay;

    private async Task<CardAssetDataSO?> GetCore(string artKey, bool acquireRef)
    {
        if (string.IsNullOrEmpty(artKey))
            return null;

        if (IsFailureSuppressed(artKey))
            return null;

        if (_entries.TryGetValue(artKey, out var existing))
        {
            Touch(artKey);
            if (acquireRef)
                existing.RefCount++;
            return existing.Asset;
        }

        AsyncOperationHandle<CardAssetDataSO> handle;
        try
        {
            handle = Addressables.LoadAssetAsync<CardAssetDataSO>(artKey);
            await handle.Task;
        }
        catch (Exception ex)
        {
            ReportArtDegraded(CollectionPanelLogReasonCode.AddressablesLoadException, artKey, ex);
            MarkFailed(artKey);
            return null;
        }

        if (handle.Status != AsyncOperationStatus.Succeeded)
        {
            ReportArtDegraded(CollectionPanelLogReasonCode.AddressablesLoadFailed, artKey, null);
            try
            {
                Addressables.Release(handle);
            }
            catch
            {
                // best-effort
            }
            MarkFailed(artKey);
            return null;
        }

        // A second concurrent Get could have populated the entry while we awaited.
        if (_entries.TryGetValue(artKey, out var raced))
        {
            try
            {
                Addressables.Release(handle);
            }
            catch
            {
                // best-effort
            }
            Touch(artKey);
            if (acquireRef)
                raced.RefCount++;
            return raced.Asset;
        }

        var node = _lru.AddFirst(artKey);
        _nodeMap[artKey] = node;
        _entries[artKey] = new CacheEntry(handle, handle.Result, acquireRef ? 1 : 0);
        Evict();
        return handle.Result;
    }

    public void Release(string artKey)
    {
        if (string.IsNullOrEmpty(artKey))
            return;
        if (_entries.TryGetValue(artKey, out var entry))
            entry.RefCount = Math.Max(0, entry.RefCount - 1);
    }

    public void DisposeAll()
    {
        foreach (var pair in _entries)
        {
            try
            {
                Addressables.Release(pair.Value.Handle);
            }
            catch (Exception ex)
            {
                BppLog.DebugEvent(
                    new BppLogEvent(
                        BppLogFeatureScope.CollectionPanel,
                        "collection_panel.cache.cleanup_failed"
                    ),
                    ex,
                    () =>
                        [
                            ("cache", CollectionCacheKind.Art),
                            ("stage", CollectionCacheCleanupStage.Release),
                            ("art_key", pair.Key),
                        ]
                );
            }
        }
        _entries.Clear();
        _lru.Clear();
        _nodeMap.Clear();
        _failedKeys.Clear();
    }

    private void Evict()
    {
        while (_lru.Count > _capacity)
        {
            // Walk from tail to head looking for an entry with refcount 0. If every entry is
            // still in use we stop and let the cache temporarily exceed _capacity rather than
            // pull the rug from under a live card.
            var node = _lru.Last;
            string? evictKey = null;
            while (node != null)
            {
                if (_entries.TryGetValue(node.Value, out var entry) && entry.RefCount <= 0)
                {
                    evictKey = node.Value;
                    break;
                }
                node = node.Previous;
            }
            if (evictKey == null)
                break;

            if (_nodeMap.TryGetValue(evictKey, out var evictNode))
            {
                _lru.Remove(evictNode);
                _nodeMap.Remove(evictKey);
            }
            if (_entries.TryGetValue(evictKey, out var evictedEntry))
            {
                _entries.Remove(evictKey);
                try
                {
                    Addressables.Release(evictedEntry.Handle);
                }
                catch (Exception ex)
                {
                    BppLog.DebugEvent(
                        new BppLogEvent(
                            BppLogFeatureScope.CollectionPanel,
                            "collection_panel.cache.cleanup_failed"
                        ),
                        ex,
                        () =>
                            [
                                ("cache", CollectionCacheKind.Art),
                                ("stage", CollectionCacheCleanupStage.EvictRelease),
                                ("art_key", evictKey),
                            ]
                    );
                }
            }
        }
    }

    private void Touch(string artKey)
    {
        if (!_nodeMap.TryGetValue(artKey, out var node))
            return;
        _lru.Remove(node);
        _lru.AddFirst(node);
    }

    private sealed class CacheEntry
    {
        public CacheEntry(
            AsyncOperationHandle<CardAssetDataSO> handle,
            CardAssetDataSO asset,
            int refCount
        )
        {
            Handle = handle;
            Asset = asset;
            RefCount = Math.Max(0, refCount);
        }

        public AsyncOperationHandle<CardAssetDataSO> Handle { get; }
        public CardAssetDataSO Asset { get; }
        public int RefCount { get; set; }
    }
}
