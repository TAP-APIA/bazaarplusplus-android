#nullable enable
using BazaarGameShared.Domain.Cards;
using BazaarPlusPlus.GameInterop.AssetLoading;
#if BPP_ANDROID
using BazaarPlusPlus.GameInterop.CardPreview;
using BazaarPlusPlus.GameInterop.ItemBoardPreview;
#endif
using TheBazaar.AppFramework;
using TheBazaar.UI;
using UnityEngine;
using UnityEngine.UI;

namespace BazaarPlusPlus.GameInterop.MonsterBoardPreview;

// A rental is registered before any native Resize/SetUp work can fail.
internal sealed class NativeBoardCardRental : IDisposable
{
    private readonly RectTransform _prefabRect;
    private readonly AspectRatioFitter? _prefabAspect;
    private bool _disposed;
    private RectTransform? _framePrefabRect;
    internal CardPreviewBase Card { get; }
    internal bool Ready { get; private set; }
#if BPP_ANDROID
    private (int Socket, int Span)? _itemPlacement;
    private LayoutElement? _boardLayout;
    private bool _ownsBoardLayout;
    private bool _previousIgnoreLayout;
    private bool _previousLayoutEnabled;

    internal void SetItemPlacement(int socket, int span) => _itemPlacement = (socket, span);

    internal void FitItem(RectTransform carpet)
    {
        if (_itemPlacement is not { } placement)
            return;
        var rect = Card.ParentRect;
        rect.SetParent(carpet, false);
        // The carpet's native layout group otherwise packs/re-sizes these owned children.
        _boardLayout = rect.GetComponent<LayoutElement>();
        _ownsBoardLayout = _boardLayout == null;
        if (_ownsBoardLayout)
            _boardLayout = rect.gameObject.AddComponent<LayoutElement>();
        _previousIgnoreLayout = _boardLayout!.ignoreLayout;
        _previousLayoutEnabled = _boardLayout.enabled;
        _boardLayout.enabled = true;
        _boardLayout.ignoreLayout = true;
        rect.anchorMin = rect.anchorMax = new Vector2(0f, .5f);
        rect.pivot = new Vector2(.5f, .5f);
        var slotWidth = carpet.rect.width / SocketedContainer.SocketCount;
        var height = carpet.rect.height;
        if (rect.TryGetComponent<AspectRatioFitter>(out var aspect))
            aspect.enabled = false;
        rect.localScale = Vector3.one;
        rect.sizeDelta = new Vector2(slotWidth * placement.Span, height);
        Card.Resize();
        Canvas.ForceUpdateCanvases();
        if (!NativeCardPreviewArtworkMeasurement.TryMeasure(rect, out var artwork))
            throw new InvalidOperationException("Native board card artwork has no layout bounds.");
        var scale = new Vector2(
            slotWidth * placement.Span / artwork.width,
            height * ItemBoardSocketLayout.FrameHeightOverSocket / artwork.height
        );
        rect.localScale = new Vector3(scale.x, scale.y, 1f);
        // Resize centers against its parent, so positioning must happen afterwards.
        rect.anchoredPosition = new Vector2(
            slotWidth * (placement.Socket + placement.Span * .5f) - artwork.center.x * scale.x,
            -artwork.center.y * scale.y
        );
    }
#endif

    internal NativeBoardCardRental(CardPreviewBase card, GameObject prefab)
    {
        Card = card;
        _prefabRect = prefab.GetComponent<RectTransform>();
        _prefabAspect = prefab.GetComponent<AspectRatioFitter>();
    }

    private void RestoreLayout()
    {
        var rect = Card.ParentRect;
        rect.anchorMin = _prefabRect.anchorMin;
        rect.anchorMax = _prefabRect.anchorMax;
        rect.pivot = _prefabRect.pivot;
        rect.sizeDelta = _prefabRect.sizeDelta;
        rect.anchoredPosition3D = _prefabRect.anchoredPosition3D;
        rect.localScale = _prefabRect.localScale;
        rect.localRotation = _prefabRect.localRotation;
        if (Card.TryGetComponent<AspectRatioFitter>(out var aspect) && _prefabAspect != null)
        {
            aspect.aspectMode = _prefabAspect.aspectMode;
            aspect.aspectRatio = _prefabAspect.aspectRatio;
            aspect.enabled = _prefabAspect.enabled;
        }
    }

    internal async Task Prepare(TCardBase template, TCardInstance instance)
    {
        RestoreLayout();
        // Native LoadFrame/LoadArt swallow exceptions. Clear their success signals first.
        ResetVisuals();
        Card.gameObject.SetActive(true);
        Card.Resize();
#if BPP_ANDROID
        await Card.SetUp(
                template,
                false,
                instance,
                new Il2CppSystem.Threading.CancellationToken()
            )
            .AsManagedTask();
#else
        await Card.SetUp(template, false, instance);
#endif
        if (
            Card._currentFrame == null
            || Card._currentFrame.transform.parent != Card._frameContainer
            || (
                Card.AsIl2Cpp<CardPreviewSkill>() != null
                    ? Card._cardImage.texture == null
                    : Card._cardMaterial == null
            )
        )
            throw new InvalidOperationException(
                $"Native preview assets are incomplete for {template.Id}: "
                    + $"frame={Card._currentFrame != null}, "
                    + $"parentMatches={Card._currentFrame != null && Card._currentFrame.transform.parent == Card._frameContainer}, "
                    + $"texture={Card._cardImage.texture != null}, material={Card._cardMaterial != null}."
            );
        if (!Services.TryGet<AssetLoader>(out var loader) || loader == null)
            throw new InvalidOperationException("Frame prefab loader is unavailable.");
        var framePrefab = await NativeGlobalAssetLoader.LoadByReferenceAsync<GameObject>(
            loader,
            Card._cardTierFrameSO.GetAssetReferenceByRarity(instance.Tier)
        );
        _framePrefabRect = framePrefab != null ? framePrefab.GetComponent<RectTransform>() : null;
        if (_framePrefabRect == null)
            throw new InvalidOperationException("Frame prefab layout is unavailable.");
        RestoreFrameLayout();
        Card.Resize();
        Ready = true;
    }

    private void ResetVisuals()
    {
        if (Card._currentFrame != null)
        {
            RestoreFrameLayout();
            Card._currentFrame.transform.SetParent(null, false);
            Card._currentFrame.PoolObject();
            Card._currentFrame = null;
        }
#if BPP_ANDROID
        Card._currentTier = ((BazaarGameShared.Domain.Core.Types.ETier?)null).ToNativeNullable();
#else
        Card._currentTier = null;
#endif
        Card._cardImage.texture = null;
        Card._cardImage.material = null;
        if (Card._cardMaterial != null)
            UnityEngine.Object.Destroy(Card._cardMaterial);
        Card._cardMaterial = null;
    }

    private void RestoreFrameLayout()
    {
        if (_framePrefabRect == null || Card._currentFrame == null)
            return;
        var rect = Card._currentFrame.GetComponent<RectTransform>();
        if (rect == null)
            return;
        rect.anchorMin = _framePrefabRect.anchorMin;
        rect.anchorMax = _framePrefabRect.anchorMax;
        rect.pivot = _framePrefabRect.pivot;
        rect.localRotation = _framePrefabRect.localRotation;
        rect.localScale = Vector3.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (Card == null)
            return;
#if BPP_ANDROID
        if (_boardLayout != null)
        {
            _boardLayout.ignoreLayout = _previousIgnoreLayout;
            _boardLayout.enabled = _previousLayoutEnabled;
            if (_ownsBoardLayout)
                UnityEngine.Object.DestroyImmediate(_boardLayout);
            _boardLayout = null;
        }
#endif
        Card.transform.SetParent(null, false);
        ResetVisuals();
        RestoreLayout();
        if (Ready)
            Card.gameObject.PoolObject();
        else
            UnityEngine.Object.Destroy(Card.gameObject);
    }
}
