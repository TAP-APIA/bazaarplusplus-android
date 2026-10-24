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
            if (instance == null)
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
                if (needsNewRef)
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
                if (needsNewRef)
                    ClearCachedMaterialAssignment(instance, marker);
                cacheSession.ArtCache.ReportDegraded(
                    CollectionPanelLogReasonCode.MaterialMissing,
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
            // CardPreviewBase._cardImage is a RawImage, not an Image: the material shades the
            // quad but the RawImage draws whatever texture it holds, and our replacement of the
            // native UpdateCardImageMaterial step never assigned one. A pooled card therefore kept
            // the previous item's texture, or drew nothing (the white card face) when the slot was
            // empty — the long-run white block this guards against. Assign both, but never write a
            // null texture over one the slot already holds.
            var cardImage = instance._cardImage;
            var artTexture = assetData.cardMaterial.mainTexture;
            if (cardImage == null)
            {
                // No RawImage slot means neither texture nor material can reach the screen. Report
                // it instead of returning as if the card had been dressed.
                cacheSession.ArtCache.ReportDegraded(
                    CollectionPanelLogReasonCode.CardImageMissing,
                    artKey,
                    null
                );
            }
            else
            {
                // Assign the texture only when the SO actually carries one: writing a null
                // mainTexture would erase a texture the slot may already hold (the native path may
                // have set a valid one), and the material alone still shades the quad.
                if (artTexture != null)
                    cardImage.texture = artTexture;
                cardImage.material = material;
            }
            visualsAssigned = true;
            if (artTexture == null)
            {
                // The SO loaded but its material has no texture to sample: the card will stay
                // blank even after this path succeeds, so say so instead of looking healthy.
                cacheSession.ArtCache.ReportDegraded(
                    CollectionPanelLogReasonCode.CardTextureMissing,
                    artKey,
                    null
                );
            }

            instance._gemGroupController?.Initialize(instance._clientCard);

            // Bounds may change once art material is assigned; invalidate the cell fit cache.
            marker.OnArtLoaded?.Invoke();
        }
        catch (Exception ex)
        {
            if (acquiredNewArtRef && !committedNewArtRef && !string.IsNullOrEmpty(acquiredArtKey))
                cacheSession.ArtCache.Release(acquiredArtKey!);
            var degradedArtKey = acquiredArtKey ?? marker.CurrentArtKey;
            // A failure before the visual fields were settled leaves the pooled card showing the
            // previous item's texture/material. Clear both halves so the next Show cannot draw
            // stale art. Best-effort like the release above: the component may have been destroyed
            // while an await was pending, so this catch must not throw a second exception.
            if (!visualsAssigned)
            {
                try
                {
                    if (instance != null)
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
        CardPreviewItem instance,
        CollectionPanelOwnedMarker marker
    )
    {
        if (!marker.CardMaterialOwnedByCache && instance._cardMaterial != null)
        {
            Object.Destroy(instance._cardMaterial);
        }

        instance._cardMaterial = null!;
        if (instance._cardImage != null)
        {
            // Clear both halves: the card is about to be pooled and re-bound to another artKey, and
            // a texture left behind would draw the previous item's art.
            instance._cardImage.texture = null;
            instance._cardImage.material = null;
        }
        marker.CardMaterialOwnedByCache = false;

        marker.ReleaseCurrentArtKey();
    }
}
