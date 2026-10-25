#nullable enable
#pragma warning disable CS0436
using BazaarPlusPlus.Game.CollectionPanel;
using BazaarPlusPlus.Game.CollectionPanel.Grid;
using HarmonyLib;
using TheBazaar.UI;
using Object = UnityEngine.Object;

namespace BazaarPlusPlus.Patches.CollectionPanel;

// Replaces CardPreviewItem.LoadArt for cards owned by the collection panel (marked with
// CollectionPanelOwnedMarker). The original loads the CardAssetDataSO straight through
// Addressables every call and synthesises a fresh Material each time — both leak across
// 1146 Item templates. Our replacement consults CollectionCardArtCache for the SO and
// CollectionCardMaterialCache for the Material, sharing both across cards that bind to the
// same artKey. Untagged cards fall through to the original implementation untouched.
[HarmonyPatch(typeof(CardPreviewItem), "LoadArt")]
internal static class CollectionItemLoadArtPatch
{
    [HarmonyPrefix]
#if BPP_ANDROID
    private static bool Prefix(
        CardPreviewItem __instance,
        bool isPremium,
        ref Il2CppSystem.Threading.Tasks.Task __result
    )
    {
        if (__instance == null)
        {
            __result = Il2CppSystem.Threading.Tasks.Task.CompletedTask;
            return false;
        }
        var marker = __instance.GetComponent<CollectionPanelOwnedMarker>();
        if (marker == null)
            return true;
        var cacheSession = marker.CacheOwner ?? CollectionCardCacheHost.ActiveSession;
        if (cacheSession == null)
            return true;
        __result = LoadArtFromCache(__instance, marker, cacheSession, isPremium).AsNativeTask();
        return false;
    }
#else
    private static bool Prefix(CardPreviewItem __instance, bool isPremium, ref Task __result)
    {
        // A scoped native preview can be released while CardPreviewBase.SetUp is still awaiting
        // LoadArt. Unity then supplies a destroyed component to this global prefix; touching it
        // before the ownership check throws instead of letting the cancelled preview disappear.
        if (__instance == null)
        {
            __result = Task.CompletedTask;
            return false;
        }

        var marker = __instance.GetComponent<CollectionPanelOwnedMarker>();
        if (marker == null)
            return true;

        var cacheSession = marker.CacheOwner ?? CollectionCardCacheHost.ActiveSession;
        if (cacheSession == null)
            return true;

        __result = LoadArtFromCache(__instance, marker, cacheSession, isPremium);
        return false;
    }
#endif

    private static async Task LoadArtFromCache(
        CardPreviewItem instance,
        CollectionPanelOwnedMarker marker,
        CollectionCardCacheSession cacheSession,
        bool isPremium
    )
    {
        string? acquiredArtKey = null;
        var acquiredNewArtRef = false;
        var committedNewArtRef = false;
        var visualsAssigned = false;
        try
        {
            var card = instance._cardData;
            if (card == null)
            {
                // Native SetUp assigns _cardData before awaiting LoadArt, so a null here is real
                // degradation, not a half-initialised card.
                cacheSession.ArtCache.ReportDegraded(
                    CollectionPanelLogReasonCode.CardDataMissing,
                    null,
                    null
                );
                // This card is still pooled and its RawImage would otherwise keep drawing the
                // previous item's texture under the frame. The intended artKey is unknowable here,
                // so clear both halves rather than leaving stale art behind.
                ClearCachedMaterialAssignment(instance, marker);
                return;
            }

            var artKey = card.ArtKey;
            if (
                string.IsNullOrEmpty(artKey)
                || string.Equals(artKey, "Invalid", StringComparison.Ordinal)
            )
            {
                cacheSession.ArtCache.ReportDegraded(
                    CollectionPanelLogReasonCode.ArtKeyMissing,
                    artKey,
                    null
                );
                ClearCachedMaterialAssignment(instance, marker);
                return;
            }

            var hasCurrentRef =
                marker.CacheOwner != null
                && string.Equals(marker.CurrentArtKey, artKey, StringComparison.Ordinal);
            var needsNewRef = !hasCurrentRef;

            var assetData = needsNewRef
                ? await cacheSession.ArtCache.Acquire(artKey)
                : await cacheSession.ArtCache.Get(artKey);
            acquiredNewArtRef = needsNewRef && assetData != null;
            acquiredArtKey = acquiredNewArtRef ? artKey : null;
            if (instance == null || marker == null)
            {
                if (acquiredNewArtRef)
                {
                    cacheSession.ArtCache.Release(artKey);
                    acquiredNewArtRef = false;
                }
                return;
            }
            if (assetData == null || assetData.cardMaterial == null)
            {
                if (acquiredNewArtRef)
                {
                    cacheSession.ArtCache.Release(artKey);
                    acquiredNewArtRef = false;
                }
                ClearCachedMaterialAssignment(instance, marker);
                // Split so the log names whether the SO never arrived (cache suppression or a
                // failed Addressables load) or the SO is present but ships no material.
                cacheSession.ArtCache.ReportDegraded(
                    assetData == null
                        ? CollectionPanelLogReasonCode.ArtAssetMissing
                        : CollectionPanelLogReasonCode.CardMaterialMissing,
                    artKey,
                    null
                );
                return;
            }

            // Check artTexture before material generation or visual assignment. The material samples
            // this texture: a null mainTexture causes a white quad on screen, so fail early and clear
            // any stale texture rather than leaving a blank card.
            var artTexture = assetData.cardMaterial.mainTexture;
            if (artTexture == null)
            {
                if (acquiredNewArtRef)
                {
                    cacheSession.ArtCache.Release(artKey);
                    acquiredNewArtRef = false;
                }
                ClearCachedMaterialAssignment(instance, marker);
                cacheSession.ArtCache.ReportDegraded(
                    CollectionPanelLogReasonCode.CardTextureMissing,
                    artKey,
                    null
                );
                return;
            }

            var material = cacheSession.MaterialCache.GetOrCreate(
                artKey,
                assetData,
                instance._cardMaterialShader,
                isPremium
            );
            if (material == null)
            {
                if (acquiredNewArtRef)
                {
                    cacheSession.ArtCache.Release(artKey);
                    acquiredNewArtRef = false;
                }
                ClearCachedMaterialAssignment(instance, marker);
                cacheSession.ArtCache.ReportDegraded(
                    CollectionPanelLogReasonCode.MaterialMissing,
                    artKey,
                    null
                );
                return;
            }

            var cardImage = instance._cardImage;
            if (cardImage == null)
            {
                if (acquiredNewArtRef)
                {
                    cacheSession.ArtCache.Release(artKey);
                    acquiredNewArtRef = false;
                }
                ClearCachedMaterialAssignment(instance, marker);
                cacheSession.ArtCache.ReportDegraded(
                    CollectionPanelLogReasonCode.CardImageMissing,
                    artKey,
                    null
                );
                return;
            }

            if (needsNewRef)
            {
                cacheSession.MaterialCache.Acquire(artKey);
                marker.ReleaseCurrentArtKey();
                marker.CacheOwner = cacheSession;
                marker.CurrentArtKey = artKey;
                committedNewArtRef = true;
                acquiredNewArtRef = false;
            }

            if (
                !marker.CardMaterialOwnedByCache
                && instance._cardMaterial != null
                && !ReferenceEquals(instance._cardMaterial, material)
            )
            {
                Object.Destroy(instance._cardMaterial);
            }

            instance._cardMaterial = material;
            marker.CardMaterialOwnedByCache = true;

            // CardPreviewBase._cardImage is a RawImage: assign the validated non-null texture
            // and the shared material together, then commit visualsAssigned.
            cardImage.texture = artTexture;
            cardImage.material = material;
            visualsAssigned = true;

            instance._gemGroupController?.Initialize(instance._clientCard);

            // Bounds may change once art material is assigned; invalidate the cell fit cache.
            marker.OnArtLoaded?.Invoke();
        }
        catch (Exception ex)
        {
            if (acquiredNewArtRef && !committedNewArtRef && !string.IsNullOrEmpty(acquiredArtKey))
                cacheSession.ArtCache.Release(acquiredArtKey!);
            var degradedArtKey = acquiredArtKey ?? marker?.CurrentArtKey;
            // A failure before the visual fields were settled leaves the pooled card showing the
            // previous item's texture/material. Clear both halves so the next Show cannot draw
            // stale art. Best-effort like the release above: the component may have been destroyed
            // while an await was pending, so this catch must not throw a second exception.
            if (!visualsAssigned)
            {
                try
                {
                    ClearCachedMaterialAssignment(instance, marker);
                }
                catch
                {
                    // best-effort
                }
            }
            cacheSession.ArtCache.ReportDegraded(
                CollectionPanelLogReasonCode.CachedLoadFailed,
                degradedArtKey,
                ex
            );
        }
    }

    private static void ClearCachedMaterialAssignment(
        CardPreviewItem? instance,
        CollectionPanelOwnedMarker? marker
    )
    {
        if (marker != null && instance != null)
        {
            try
            {
                if (!marker.CardMaterialOwnedByCache && instance._cardMaterial != null)
                {
                    Object.Destroy(instance._cardMaterial);
                }
            }
            catch
            {
                // best-effort
            }
        }

        if (instance != null)
        {
            try
            {
                instance._cardMaterial = null!;
                var cardImage = instance._cardImage;
                if (cardImage != null)
                {
                    // Clear both halves: the card is about to be pooled and re-bound to another artKey, and
                    // a texture left behind would draw the previous item's art.
                    cardImage.texture = null;
                    cardImage.material = null;
                }
            }
            catch
            {
                // best-effort
            }
        }

        if (marker != null)
        {
            marker.CardMaterialOwnedByCache = false;
            marker.ReleaseCurrentArtKey();
        }
    }
}
