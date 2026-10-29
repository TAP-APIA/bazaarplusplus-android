#nullable enable
using BazaarGameClient.Domain.Models.Cards;
using BazaarGameShared.Domain.Core.Types;
using BazaarPlusPlus.Game.CollectionPanel.Grid;
using BazaarPlusPlus.Game.CollectionPanel.Tooltips;
using BazaarPlusPlus.GameInterop.CardPreview;
using BazaarPlusPlus.GameInterop.Tooltips;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using BazaarPlusPlus.Infrastructure.UiTokens;
using TheBazaar.Tooltips;
using TheBazaar.UI;
using TheBazaar.UI.Tooltips;
using UnityEngine;

namespace BazaarPlusPlus.Game.CollectionPanel;

internal sealed class CollectionInspectionModule
{
    private readonly CollectionInspectionState _state = new();
    private readonly CollectionInspectionCardAnchor _cardAnchor = new();
    private INativeCardPreviewHost? _host;
    private INativeCardPreviewHost? _globalHost;
    private Action<bool>? _blockInput;
    private Func<Rect>? _sourceBounds;
    private CardTooltipController? _controller;
    private NativeInspectionPresentation? _presentation;
    private InspectionVariantPreviewState? _variants;
    private InspectionVariantBar? _variantBar;
    private CardTooltipData? _inspectionData;
    private INativeCardPreviewScope? _scope;
    private INativeCardPreviewSession? _card;
    private InspectionPreviewOwner? _previewOwner;
    private NativeCardCellBoundsCache _bounds = new();
    private Rect _origin;
    private float _animation;
    private bool _cardRevealed;
    private bool _measurementPending;
    private float _measurementDeadline;

    public bool BlocksInput => _state.BlocksInput(Time.frameCount);
    public bool IsOpen => _state.IsOpen;

    internal void InitializeGlobalHost(INativeCardPreviewHost host) => _globalHost = host;

    public void OpenHovered(Guid templateId)
    {
        var controller = TheBazaar.Data.TooltipParentComponent?.CardTooltipController;
        var data = controller?.CurrentTooltipData.TryCast<CardTooltipData>();
        var rejection =
            BlocksInput ? "input_blocked"
            : controller == null ? "controller_missing"
            : !controller.isActiveAndEnabled ? "controller_inactive"
            : controller.IsLocked ? "already_locked"
            : data == null ? "card_data_missing"
            : data.CardInstance.TemplateId.ToSystemGuid() != templateId ? "card_mismatch"
            : !CollectionTierTooltipRegistry.Contains(data.CardInstance) ? "card_unregistered"
            : null;
        if (rejection != null)
            return;
#if BPP_ANDROID
        // The mobile toggle can return without locking even with matching visible data.
        // This is an explicit inspection intent, so use the native lock operation.
        controller!.Lock();
#else
        controller!.LockTooltipToggle();
#endif
    }

#if BPP_ANDROID
    // The native tooltip spawns asynchronously after a hover, so allow it a moment to appear.
    private const float PreviewRequestSeconds = 1f;
    private CardPreviewBase? _previewCard;
    private bool _previewPending;
    private float _previewRequestDeadline;
    private Rect _previewOrigin;
    private IntPtr _acceptedPreview;

    public bool HasPendingPreview => _previewPending;

    // Touch lifts end a native hover. The long-pressed card keeps its hover until the request is
    // dropped or its inspection closes, so that release cannot hide or unlock the tooltip.
    public bool HoldsPreviewHover(CardPreviewBase card) =>
        _previewCard != null && card != null && card.Pointer == _previewCard.Pointer;

    // Live Build and History boards have no hover on touch. A long press asks the card for its
    // native tooltip; the lock follows once the controller shows exactly that card's data.
    public void RequestPreviewInspection(CardPreviewBase card, Rect origin)
    {
        if (BlocksInput || card == null || card._tooltipData == null)
            return;
        ReleasePreviewHover();
        _previewCard = card;
        _previewPending = true;
        _previewRequestDeadline = Time.unscaledTime + PreviewRequestSeconds;
        _previewOrigin = origin;
        try
        {
            card.OnHover();
        }
        catch (Exception ex)
        {
            Report(ex);
            ReleasePreviewHover();
            return;
        }
        PumpPreviewRequest();
    }

    // Idempotent: the Collection, Live Build and History ticks may all call it in one frame.
    public void PumpPreviewRequest()
    {
        if (!_previewPending)
            return;
        var card = _previewCard;
        if (
            card == null
            || BlocksInput
            || Time.unscaledTime > _previewRequestDeadline
            || !card.gameObject.activeInHierarchy
            || card._tooltipData == null
        )
        {
            ReleasePreviewHover();
            return;
        }
        var controller = TheBazaar.Data.TooltipParentComponent?.CardTooltipController;
        // Read the card's data every frame: a hover may rebind it.
        if (
            controller == null
            || !controller.isActiveAndEnabled
            || controller.IsLocked
            || controller.CurrentTooltipData == null
            || controller.CurrentTooltipData.Pointer != card._tooltipData.Pointer
        )
            return;
        _previewPending = false;
        // Preview tooltip data is neither a registered Collection card nor an in-run ItemCard,
        // so OnLocked accepts it only for this explicit request.
        _acceptedPreview = card._tooltipData.Pointer;
        try
        {
            controller.Lock();
        }
        finally
        {
            _acceptedPreview = IntPtr.Zero;
        }
        if (!_state.IsOpen)
            ReleasePreviewHover();
    }

    private void ReleasePreviewHover()
    {
        var card = _previewCard;
        _previewCard = null;
        _previewPending = false;
        if (card == null)
            return;
        try
        {
            card.OnHoverOut();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }
#endif

    public void Attach(
        INativeCardPreviewHost host,
        Action<bool> blockInput,
        Func<Rect> sourceBounds
    )
    {
        _host = host;
        _blockInput = blockInput;
        _sourceBounds = sourceBounds;
    }

    public void Detach()
    {
        Close();
        _host = null;
        _blockInput = null;
        _sourceBounds = null;
    }

    public void OnLocked(CardTooltipController controller)
    {
        var host = _host ?? _globalHost;
        if (
            host == null
            || controller == null
            || !controller.IsLocked
            || controller.CurrentTooltipData.TryCast<CardTooltipData>() is not { } data
            || (!CollectionTierTooltipRegistry.Contains(data.CardInstance)
                && data.CardInstance.TryCast<ItemCard>() == null
#if BPP_ANDROID
                && data.Pointer != _acceptedPreview
#endif
            )
        )
            return;
        Close();
        _controller = controller;
        _inspectionData = data;
        _origin = _sourceBounds?.Invoke() ?? default;
#if BPP_ANDROID
        if (_acceptedPreview != IntPtr.Zero && data.Pointer == _acceptedPreview)
            _origin = _previewOrigin;
#endif
        _state.Open();
        _cardAnchor.Reset();
        _animation = 0f;
        try
        {
            _blockInput?.Invoke(true);
            _presentation = new NativeInspectionPresentation(controller);
            _previewOwner = new InspectionPreviewOwner(_presentation.CardRoot);
            _scope = host.OpenScope(_previewOwner);
            CreateVariantControls(controller, data);
            BppLog.InfoEvent(
                new BppLogEvent(
                    BppLogFeatureScope.CollectionPanel,
                    "collection_panel.inspection.opened"
                )
            );
            OnPreviewChanged(controller, data);
            RefreshLayout(0f);
        }
        catch (Exception ex)
        {
            Report(ex);
            Close();
        }
    }

    // The selector is optional: a card with one quality and no enchantments is still inspectable,
    // and a failure while building chips must not abort an otherwise valid inspection.
    private void CreateVariantControls(CardTooltipController controller, CardTooltipData data)
    {
        if (_presentation == null)
            return;
        var options = InspectionVariantOptions.Build(data);
        if (options == null)
        {
            // Not every card has a second quality or a second enchantment; reporting keeps the
            // difference between "nothing to choose" and "the selector failed to build" visible.
            BppLog.InfoEvent(
                new BppLogEvent(
                    BppLogFeatureScope.CollectionPanel,
                    "collection_panel.inspection.variants.unavailable"
                ),
                ("registered", CollectionTierTooltipRegistry.Contains(data.CardInstance))
            );
            return;
        }
        try
        {
            _variants = new InspectionVariantPreviewState(data, options);
            _variantBar = _presentation.CreateVariantBar(
                options,
                _variants.SelectedTier,
                _variants.SelectedEnchantment
            );
            if (_variantBar == null)
            {
                BppLog.WarnEvent(
                    new BppLogEvent(
                        BppLogFeatureScope.CollectionPanel,
                        "collection_panel.inspection.variants.creation_failed"
                    ),
                    ("tiers", options.Tiers.Count),
                    ("enchantments", options.Enchantments.Count)
                );
                return;
            }
            BppLog.InfoEvent(
                new BppLogEvent(
                    BppLogFeatureScope.CollectionPanel,
                    "collection_panel.inspection.variants.ready"
                ),
                ("tiers", options.Tiers.Count),
                ("enchantments", options.Enchantments.Count)
            );
            _variantBar.TierSelected += OnTierSelected;
            _variantBar.EnchantmentSelected += OnEnchantmentSelected;
            _variantBar.ResetRequested += OnResetSelected;
        }
        catch (Exception ex)
        {
            Report(ex);
            DestroyVariantControls();
        }
    }

    private void OnTierSelected(ETier tier)
    {
        if (_variants?.SelectTier(tier) != true)
            return;
        ApplyVariantSelection();
    }

    private void OnEnchantmentSelected(EEnchantmentType? enchantment)
    {
        if (_variants?.SelectEnchantment(enchantment) != true)
            return;
        ApplyVariantSelection();
    }

    private void OnResetSelected()
    {
        if (_variants?.Reset() != true)
            return;
        ApplyVariantSelection();
    }

    /// <summary>
    /// Pushes the selected quality/enchantment into both inspection surfaces: the native tooltip
    /// text and the 3D card preview. The native call also drives <c>OnPreviewChanged</c>, so the
    /// card model and the description never disagree about the selected variant.
    /// </summary>
    private void ApplyVariantSelection()
    {
        if (_variants == null || _controller == null)
            return;
        // The description is about to change, so a popup describing the old copy must not linger.
        _presentation?.HideNestedTooltip();
        var data = _variants.Data;
        _inspectionData = data;
        _variantBar?.SyncSelection(_variants.SelectedTier, _variants.SelectedEnchantment);
        if (!NativeCardTooltipContentRefresher.TryApply(_controller, data))
        {
            Report(
                new InvalidOperationException(
                    "Native inspection tooltip rejected the selected quality/enchantment preview."
                )
            );
            return;
        }
        _measurementPending = false;
        _bounds = new NativeCardCellBoundsCache();
        RefreshLayout(0f);
    }

    private void DestroyVariantControls()
    {
        if (_variantBar != null)
        {
            _variantBar.TierSelected -= OnTierSelected;
            _variantBar.EnchantmentSelected -= OnEnchantmentSelected;
            _variantBar.ResetRequested -= OnResetSelected;
            _presentation?.ReleaseVariantBar(_variantBar);
            _variantBar = null;
        }
        _variants?.Dispose();
        _variants = null;
    }

    public void OnPreviewChanged(CardTooltipController controller, CardTooltipData data)
    {
        if (!ReferenceEquals(controller, _controller) || _scope == null)
            return;
        // The tooltip applies the preview data itself, so the 3D model must follow the same
        // selection; otherwise switching quality would only change the text.
        _inspectionData = data;
        var card = data.CardInstance;
        Dictionary<ECardAttributeType, int> attributes;
        try
        {
            attributes =
                card.Attributes != null
                    ? new Dictionary<ECardAttributeType, int>(card.Attributes.ToManagedDictionary())
                    : new Dictionary<ECardAttributeType, int>();
        }
        catch
        {
            attributes = new Dictionary<ECardAttributeType, int>();
        }

        var subject = new NativeCardPreviewSubject
        {
            TemplateId = card.TemplateId.ToSystemGuid(),
            Tier = card.Tier,
            EnchantmentType = BazaarPlusPlus.GameInterop.Cards.NativeCardOptionalFields.Enchantment(
                card
            ),
            Attributes = attributes,
            InstanceIdPrefix = "bpp-inspection",
        };
        _ = AcquirePreviewAsync(_scope, subject, _state.Select());
    }

    public void OnNativeClosed(CardTooltipController controller, bool unlock = false)
    {
        if (ReferenceEquals(controller, _controller))
            Close(unlock);
    }

    public void Close() => Close(unlock: true);

    private void Close(bool unlock)
    {
        if (!_state.IsOpen)
            return;
        _state.Close(Time.frameCount);
        _cardAnchor.Reset();
        // The nested popup is a native sibling of the description, so it must go down with it.
        _presentation?.HideNestedTooltip();
        var controller = _controller;
        _controller = null;
        // Stop native ownership synchronously before destroying anything its lock can reference.
        try
        {
            if (unlock && controller != null && controller.IsLocked)
                controller.Unlock();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
        finally
        {
            DestroyVariantControls();
            _inspectionData = null;
#if BPP_ANDROID
            // After the unlock, so the held hover-out cannot race the lock it was protecting.
            ReleasePreviewHover();
#endif
            _card?.Dispose();
            _card = null;
            _previewOwner = null;
            var scope = _scope;
            var presentation = _presentation;
            _scope = null;
            _presentation = null;
            presentation?.ReleaseNative();
            if (scope != null)
                _ = DisposeScopeAsync(scope, presentation);
            else
                presentation?.Dispose();
            // Input remains blocked until a later frame, so dismissing cannot click through.
        }
    }

    public void Tick(float dt)
    {
#if BPP_ANDROID
        PumpPreviewRequest();
#endif
        if (
            _state.IsOpen
            && (_controller == null || !_controller.isActiveAndEnabled || !_controller.IsLocked)
        )
            Close(unlock: false);
        _blockInput?.Invoke(BlocksInput);
        RefreshLayout(dt);
        // A tap that opens or closes a nested tooltip is consumed, so it must not also read as a
        // background dismissal within the same frame.
        if (_presentation?.PollTap() == true)
            return;
        if (_presentation?.PollDismiss() == true)
        {
            BppLog.InfoEvent(
                new BppLogEvent(
                    BppLogFeatureScope.CollectionPanel,
                    "collection_panel.inspection.background_dismissed"
                )
            );
            Close();
        }
    }

    private void PositionVariantBar()
    {
        if (_presentation == null || _variantBar == null)
            return;
        _presentation.PositionVariantBar(_variantBar, VariantBarGap);
    }

#if BPP_ANDROID
    /// <summary>
    /// Centers the BPP quality/enchantment band under the enlarged card in the left half.
    ///
    /// Android keeps the game's own drawer on the right, so the description is no longer the
    /// anchor the band hangs under; the enlarged card is.
    /// </summary>
    private void PositionVariantBarUnderCard(Rect cardCell)
    {
        if (_variantBar == null)
            return;
        var size = _variantBar.Measure();
        var centerX = cardCell.x + cardCell.width * .5f;
        // Cell rects run top-down from the screen top; the band is placed in screen coordinates (bottom-up).
        var cardBottom = Screen.height - (cardCell.y + cardCell.height);
        var safeBottom = Mathf.Max(0f, Screen.safeArea.yMin);
        var minTop = safeBottom + size.y + 6f;
        var top = Mathf.Max(minTop, cardBottom - VariantBarGap);
        _variantBar.Root.position = new Vector3(centerX, top, 0f);
    }

    // Use the native drawer's measured screen edge. Until it is measurable, split the
    // current safe content evenly; no physical resolution identifies a device class.
    private float AndroidDrawerLeft(Rect content)
    {
        if (
            _presentation != null
            && _presentation.TryGetNativeTrayScreenRect(out var tray)
            && tray.xMin > content.xMin + content.width * .2f
            && tray.xMin < content.xMax - content.width * .1f
            && tray.width > content.width * .2f
        )
            return tray.xMin;
        return content.center.x;
    }

    private CollectionInspectionLayout AndroidInspectionLayout(
        float visualWidth,
        float visualHeight
    )
    {
        var content = AndroidOverlayViewport.ContentPixels;
        var laneWidth = Mathf.Max(1f, AndroidDrawerLeft(content) - content.xMin);
        _variantBar?.FitWidth(laneWidth * .95f);
        var selectorHeight = _variantBar?.Measure().y ?? 0f;
        return CollectionInspectionLayout.ForDrawer(
            content.xMin,
            Screen.height - content.yMax,
            laneWidth,
            content.height,
            visualWidth,
            visualHeight,
            selectorHeight,
            VariantBarGap,
            AndroidOverlayViewport.UiScale
        );
    }

    private Rect AndroidPlaceholderCard()
    {
        var layout = AndroidInspectionLayout(300f, 500f);
        return new Rect(layout.CardX, layout.CardY, layout.CardWidth, layout.CardHeight);
    }
#endif

    /// <summary>
    /// Vertical gap between the description and the selector, scaled to the viewport so the band
    /// stays tactile on a phone without drifting off a tablet's larger screen.
    /// </summary>
    private static float VariantBarGap =>
#if BPP_ANDROID
        AndroidOverlayViewport.Read().TouchScale * 8f;
#else
        Mathf.Clamp(Screen.height * .014f, 10f, 26f);
#endif

    private void RefreshLayout(float dt)
    {
        if (_presentation == null)
            return;
#if !BPP_ANDROID
        if (_card != null && _cardAnchor.NeedsLayout(Screen.width, Screen.height))
            _bounds.InvalidateOnScaleDirty();
#endif
        if (_card != null && !_bounds.IsValid)
        {
            if (!_measurementPending)
            {
                _measurementPending = true;
                _measurementDeadline = Time.unscaledTime + 1f;
            }
            NativeCardCellFitter.PrepareGridRect(_card.Rect);
            NativeCardPreviewArtworkMeasurement.PrepareLayoutSize(
                _card.Rect,
                NativeCardCellFitter.FallbackNativeCardHeight
            );
            Canvas.ForceUpdateCanvases();
            if (!NativeCardPreviewArtworkMeasurement.TryMeasure(_card.Rect, out var artwork))
            {
                if (Time.unscaledTime >= _measurementDeadline)
                {
                    Report(
                        new InvalidOperationException(
                            "Native inspection artwork did not become measurable. "
                                + NativeCardPreviewArtworkMeasurement.DescribeLayout(_card.Rect)
                        )
                    );
                    Close();
                }
                return;
            }
            _measurementPending = false;
            _bounds.Store(
                new CardVisualBounds(
                    artwork.width,
                    artwork.height,
                    artwork.center.x,
                    artwork.center.y
                ),
                null
            );
        }
#if BPP_ANDROID
        _presentation.MeasureDescription(Screen.width * .92f, Screen.height * .74f);
#else
        var detail = _presentation.MeasureDescription(Screen.width * .92f, Screen.height * .74f);
#endif
        if (_card == null)
        {
#if BPP_ANDROID
            PositionVariantBarUnderCard(AndroidPlaceholderCard());
#else
            _presentation.PositionDescription(Screen.width / 2f, Screen.height / 2f, 1f);
            _presentation.PositionVariantBar(_variantBar, VariantBarGap);
#endif
            return;
        }
#if !BPP_ANDROID
        NativeCardCellFitter.ApplyScale(
            _card.Rect,
            new CollectionGridRect(0f, 0f, Screen.width * .50f, Screen.height * .72f),
            0f,
            _bounds
        );
#endif
        if (!_bounds.TryGet(out var visual, out _))
            return;
#if BPP_ANDROID
        var layout = AndroidInspectionLayout(visual.Width, visual.Height);
#else
        var baseScale = _card.Rect.localScale;
        var layout = CollectionInspectionLayout.ForContent(
            Screen.width,
            Screen.height,
            visual.Width * baseScale.x,
            visual.Height * baseScale.y,
            detail.x,
            detail.y
        );
        _presentation.PositionDescription(layout.DetailCenterX, layout.DetailCenterY, layout.Scale);
        PositionVariantBar();
#endif
#if BPP_ANDROID
        var placement = new CollectionInspectionCardPlacement(
            layout.CardX,
            layout.CardY,
            layout.CardWidth,
            layout.CardHeight
        );
#else
        var placement = _cardAnchor.Resolve(Screen.width, Screen.height, layout);
#endif
        _animation = Mathf.Min(1f, _animation + dt / .25f);
        var progress = 1f - Mathf.Pow(1f - _animation, 3f);
        var target = new Rect(placement.X, placement.Y, placement.Width, placement.Height);
        var origin = _origin.width > 0f ? _origin : target;
        var rect = new Rect(
            Vector2.Lerp(origin.position, target.position, progress),
            Vector2.Lerp(origin.size, target.size, progress)
        );
#if BPP_ANDROID
        PositionVariantBarUnderCard(rect);
#endif
        var cell = new CollectionGridRect(rect.x, rect.y, rect.width, rect.height);
        var animatedPlacement = new CollectionInspectionCardPlacement(
            rect.x,
            rect.y,
            rect.width,
            rect.height
        );
        var scale = animatedPlacement.ScaleFor(visual.Width, visual.Height);
        _card.Rect.localScale = new Vector3(scale.X, scale.Y, 1f);
        NativeCardCellFitter.Reposition(_card.Rect, cell, 0f, _bounds, allowMeasure: false);
        if (!_cardRevealed)
        {
            if (_previewOwner?.Reveal(_card.Root) != true)
            {
                Report(
                    new InvalidOperationException(
                        "Native inspection visibility lease was unavailable."
                    )
                );
                Close();
                return;
            }
            _cardRevealed = true;
        }
    }

    private async Task AcquirePreviewAsync(
        INativeCardPreviewScope scope,
        NativeCardPreviewSubject subject,
        int revision
    )
    {
        try
        {
            var result = await scope.AcquireAsync(subject);
            if (result.Session == null)
            {
                if (_state.CanPublish(revision))
                    Close();
                return;
            }
            if (!_state.CanPublish(revision))
            {
                result.Session.Dispose();
                return;
            }
            _card?.Dispose();
            _card = result.Session;
            _bounds = new NativeCardCellBoundsCache();
            _cardRevealed = false;
            _measurementPending = false;
            var shown = _card.Show();
            if (shown.Status != NativePreviewActionStatus.Applied)
            {
                Report(
                    shown.Failure?.Exception
                        ?? new InvalidOperationException(
                            $"Native inspection preview activation failed: {shown.Status}."
                        )
                );
                Close();
                return;
            }
            RefreshLayout(0f);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Report(ex);
            if (_state.CanPublish(revision))
                Close();
        }
    }

    private static async Task DisposeScopeAsync(
        INativeCardPreviewScope scope,
        NativeInspectionPresentation? presentation
    )
    {
        try
        {
            await scope.DisposeAsync();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
        finally
        {
            presentation?.Dispose();
        }
    }

    private static void Report(Exception ex) =>
        BppLog.WarnEvent(
            new BppLogEvent(
                BppLogFeatureScope.CollectionPanel,
                "collection_panel.inspection.failed",
                storm: ["reason_code"]
            ),
            ex,
            ("reason_code", CollectionPanelLogReasonCode.NativePreviewRuntimeFailed)
        );

    private sealed class InspectionPreviewOwner(Transform parent) : INativeCardPreviewOwner
    {
        private readonly Dictionary<GameObject, (CanvasGroup Group, float Alpha)> _visibility =
            new();
        public int Layer => CollectionGridOverlay.DefaultLayer;
        public bool UsePremiumVisuals => true;

        public Transform ResolveParent(NativeCardPreviewSubject subject) => parent;

        public void PrepareWhileInactive(NativeCardPreviewOwnerContext context)
        {
            var cacheSession = CollectionCardCacheHost.ActiveSession;
            if (cacheSession != null)
            {
                var marker = context.Root.GetComponent<CollectionPanelOwnedMarker>();
                if (marker == null)
                    marker = context.Root.AddComponent<CollectionPanelOwnedMarker>();
                marker.CacheOwner = cacheSession;
            }
        }

        public void OnAcquired(NativeCardPreviewOwnerContext context)
        {
            var group =
                context.Root.GetComponent<CanvasGroup>()
                ?? context.Root.AddComponent<CanvasGroup>();
            _visibility.Add(context.Root, (group, group.alpha));
            group.alpha = 0f;
            foreach (
                var graphic in context.Root.GetComponentsInChildren<UnityEngine.UI.Graphic>(true)
            )
                graphic.raycastTarget = false;
        }

        public bool Reveal(GameObject root)
        {
            if (!_visibility.TryGetValue(root, out var state) || state.Group == null)
                return false;
            state.Group.alpha = 1f;
            return true;
        }

        public void BeforeRelease(NativeCardPreviewOwnerContext context)
        {
            if (_visibility.Remove(context.Root, out var state) && state.Group != null)
                state.Group.alpha = state.Alpha;
            var marker = context.Root.GetComponent<CollectionPanelOwnedMarker>();
            if (marker != null)
            {
                marker.ReleaseCachedArt(context.Root.GetComponent<CardPreviewBase>());
            }
            // Keep the neutral component until the pooled root is destroyed. Deferred
            // Destroy could remove a new lease's alpha gate after same-frame reuse.
        }

        public void ReportFailure(NativeCardPreviewFailure failure)
        {
            if (failure.Exception != null)
                Report(failure.Exception);
        }
    }
}
