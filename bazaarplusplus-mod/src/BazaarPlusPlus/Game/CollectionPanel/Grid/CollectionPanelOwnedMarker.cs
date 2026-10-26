#nullable enable
using TheBazaar.UI;
using UnityEngine;

namespace BazaarPlusPlus.Game.CollectionPanel.Grid;

// Marker MonoBehaviour attached to every card instance the collection pool instantiates.
// Used by Harmony patches to gate the cache + material-sharing replacements so the patches
// only affect cards owned by this panel — HistoryPanel and live shop/board cards run the
// original game code untouched.
//
// CurrentArtKey records the artKey the card's cached material ref points at. CacheOwner records
// the exact cache session that acquired the ref so delayed destruction cannot release through a
// newer panel runtime. CardMaterialOwnedByCache tracks ownership of the _cardMaterial field
// itself: false means native created it and native OnDestroy must still destroy it.
//
// OnArtLoaded is set by the grid virtualizer so CollectionItemLoadArtPatch can invalidate the
// per-cell visual-bounds cache when material assignment finishes (bounds may change).
internal sealed class CollectionPanelOwnedMarker : MonoBehaviour
{
    public CollectionNativeCardPreviewOwner? PreviewOwner;
    public CollectionCardCacheSession? CacheOwner;
    public string? CurrentArtKey;
    public bool CardMaterialOwnedByCache;
    public bool TooltipRegistered;
    public Action? OnArtLoaded;

    // Inspection previews share this cache without a grid PreviewOwner. Detach their borrowed
    // material before pool return or native OnDestroy, which otherwise destroys _cardMaterial.
    public void ReleaseCachedArt(CardPreviewBase? card)
    {
        if (CardMaterialOwnedByCache && card != null)
        {
            card._cardMaterial = null!;
            if (card._cardImage != null)
            {
                card._cardImage.texture = null;
                card._cardImage.material = null;
            }
        }
        CardMaterialOwnedByCache = false;
        ReleaseCurrentArtKey();
    }

    public void ReleaseCurrentArtKey()
    {
        if (CacheOwner != null && !string.IsNullOrEmpty(CurrentArtKey))
        {
            CacheOwner.ArtCache.Release(CurrentArtKey!);
            CacheOwner.MaterialCache.Release(CurrentArtKey!);
        }

        CurrentArtKey = null;
        CacheOwner = null;
    }
}
