using System.Reflection;
using BazaarPlusPlus.Game.CollectionPanel.Grid;
using BazaarPlusPlus.GameInterop.CardPreview;
using BazaarPlusPlus.Patches.CollectionPanel;
using TheBazaar.UI;

internal static class CollectionMaterialLifetimeTests
{
    public static void Run()
    {
        // Both renderers borrow one cache entry. Simulate native destruction only after running
        // the real Harmony prefix, including a setup rollback with no BeforeRelease callback.
        foreach (var releaseFirst in new[] { false, true })
        {
            var shared = new UnityEngine.Material();
            var cache = new CollectionCardCacheSession();
            var marker = new CollectionPanelOwnedMarker
            {
                CacheOwner = cache,
                CurrentArtKey = "same-art",
                CardMaterialOwnedByCache = true,
                PreviewOwner = null, // Inspection is intentionally not a grid owner.
                TooltipRegistered = false,
                OnArtLoaded = null,
            };
            var inspection = new CardPreviewBase { Marker = marker, _cardMaterial = shared };
            inspection._cardImage.material = shared;
            inspection._cardImage.texture = new object();
            var grid = new CardPreviewBase { _cardMaterial = shared };
            grid._cardImage.material = shared;

            if (releaseFirst)
                marker.ReleaseCachedArt(inspection); // Models the owner's release operation only.
            DestroyNative(inspection);
            DestroyNative(inspection); // Cleanup must be idempotent.

            Check(!shared.Destroyed, "Closing inspection destroyed the grid's shared material.");
            Check(ReferenceEquals(grid._cardImage.material, shared), "Grid art changed.");
            Check(inspection._cardMaterial == null, "Borrowed native material field survived.");
            Check(inspection._cardImage.material == null, "Pooled material assignment survived.");
            Check(inspection._cardImage.texture == null, "Pooled texture assignment survived.");
            Check(marker.CacheOwner == null && marker.CurrentArtKey == null, "Lease survived.");
            Check(!marker.CardMaterialOwnedByCache, "Ownership flag survived.");
            Check(cache.ArtCache.Releases == 1, "Art lease released other than exactly once.");
            Check(cache.MaterialCache.Releases == 1, "Material lease released other than once.");
        }

        var native = new UnityEngine.Material();
        DestroyNative(
            new CardPreviewBase
            {
                Marker = new CollectionPanelOwnedMarker(),
                _cardMaterial = native,
            }
        );
        Check(native.Destroyed, "Native-owned material must still be destroyed by the game.");
        var unmarked = new UnityEngine.Material();
        DestroyNative(new CardPreviewBase { _cardMaterial = unmarked });
        Check(unmarked.Destroyed, "Unmarked native cards must remain untouched.");
        foreach (var lifecycleFirst in new[] { false, true })
            RunReacquireAndDestroy(lifecycleFirst);
        Console.WriteLine(
            "Inspection release/setup rollback preserve shared grid art; native ownership and refcounts pass."
        );
        Console.WriteLine(
            "Pool return/reacquisition preserve lease isolation under both real destroy-prefix orders."
        );
    }

    private static void RunReacquireAndDestroy(bool lifecycleFirst)
    {
        var shared = new UnityEngine.Material();
        var marker = new CollectionPanelOwnedMarker();
        var card = new CardPreviewBase { Marker = marker };
        var firstCache = new CollectionCardCacheSession();
        var secondCache = new CollectionCardCacheSession();
        var callbacks = 0;

        BindLease(firstCache);
        // Model pool return: disarm the first lease, detach its art, then reuse the same root.
        NativeCardPreviewLifecycle.Disarm(card.gameObject);
        marker.ReleaseCachedArt(card);
        Check(firstCache.ArtCache.Releases == 1, "First art lease was not returned.");
        Check(firstCache.MaterialCache.Releases == 1, "First material lease was not returned.");

        BindLease(secondCache);
        foreach (var iteration in new[] { 0, 1 })
        {
            if (lifecycleFirst)
            {
                InvokePrefix(typeof(NativeCardPreviewLifecyclePatch), card);
                InvokePrefix(typeof(CollectionCardPreviewDestroyPatch), card);
            }
            else
            {
                InvokePrefix(typeof(CollectionCardPreviewDestroyPatch), card);
                InvokePrefix(typeof(NativeCardPreviewLifecyclePatch), card);
            }
            SimulateNativeDestroy(card);
            Check(callbacks == 1, $"Destroy callbacks repeated at iteration {iteration}.");
        }

        Check(!shared.Destroyed, "Prefix ordering destroyed shared material after reacquisition.");
        Check(firstCache.ArtCache.Releases == 1, "Old cache was released during the next lease.");
        Check(firstCache.MaterialCache.Releases == 1, "Old material lease was released twice.");
        Check(secondCache.ArtCache.Releases == 1, "Reacquired art lease was not released once.");
        Check(
            secondCache.MaterialCache.Releases == 1,
            "Reacquired material lease was not released once."
        );
        Check(
            card._cardImage.material == null && card._cardImage.texture == null,
            "Reacquired card kept borrowed art after destruction."
        );

        void BindLease(CollectionCardCacheSession cache)
        {
            // Model LoadArt's successful cache assignment. The real lifecycle binder/prefixes
            // below are compiled unchanged; this test does not instantiate InspectionPreviewOwner.
            marker.CacheOwner = cache;
            marker.CurrentArtKey = "same-art";
            marker.CardMaterialOwnedByCache = true;
            card._cardMaterial = shared;
            card._cardImage.material = shared;
            card._cardImage.texture = new object();
            NativeCardPreviewLifecycle.Bind(card.gameObject, () =>
            {
                callbacks++;
                marker.ReleaseCachedArt(card);
            });
        }
    }

    private static void DestroyNative(CardPreviewBase card)
    {
        InvokePrefix(typeof(CollectionCardPreviewDestroyPatch), card);
        SimulateNativeDestroy(card);
    }

    private static void InvokePrefix(Type patch, CardPreviewBase card) =>
        patch
            .GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [card]);

    private static void SimulateNativeDestroy(CardPreviewBase card)
    {
        // Android CardPreviewBase.OnDestroy at RVA 0x7528FEC destroys a non-null _cardMaterial.
        if (card._cardMaterial != null)
            card._cardMaterial.Destroyed = true;
    }

    private static void Check(bool passed, string message)
    {
        if (!passed)
            throw new InvalidOperationException(message);
    }
}
