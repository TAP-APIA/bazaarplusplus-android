#nullable enable
using BazaarGameShared.Domain.Core.Types;
using BazaarPlusPlus.Game.LiveBuildPanel.Data;
using BazaarPlusPlus.Game.Supporters.Ui;
using BazaarPlusPlus.GameInterop;
using BazaarPlusPlus.GameInterop.AssetLoading;
using BazaarPlusPlus.GameInterop.Fonts;
using BazaarPlusPlus.GameInterop.HeroPortraits;
using BazaarPlusPlus.GameInterop.ItemBoardPreview;
using BazaarPlusPlus.GameInterop.MonsterBoardPreview;
using BazaarPlusPlus.Infrastructure.UiTokens;
using BazaarPlusPlus.Localization;
using TheBazaar.AppFramework;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BazaarPlusPlus.Game.LiveBuildPanel.Ui;

internal sealed class LiveBuildPanelView : IDisposable
{
    private const string Art = "Assets/TheBazaar/Art/UI/Buttons/Btn_Rectangle/";
    private static readonly string[] Addresses =
    [
        Art + "Btn_Rct_Frame_S_TUI.png",
        Art + "Btn_Rct_Brown_S_Center_Active_TUI.png",
        Art + "Btn_Rct_Blue_S_Center_Active_TUI.png",
    ];
    private static readonly Color Ink = new(.96f, .89f, .73f);
    private static readonly Color Muted = new(.69f, .63f, .53f);

    private sealed class Row
    {
        internal RectTransform Region = null!;
        internal RectTransform Header = null!;
        internal RectTransform Divider = null!;
        internal int Index;
        internal TextMeshProUGUI Title = null!;
        internal TextMeshProUGUI Status = null!;
        internal Rect Bounds;
        internal string Message = string.Empty;
        internal string MarkerText = string.Empty;
        internal readonly List<RectTransform> Markers = new();
        internal int VisibleMarkers;
    }

    private readonly Transform _parent;
    private readonly Action _close;
    private readonly Action _previous;
    private readonly Action _next;
    private readonly Action _refresh;
    private readonly Dictionary<BppItemBoardId, Row> _rows = new();
    private readonly List<(Image Image, int Index)> _art = new();
    private NativeGameTypography.OwnedTextPreparation? _font;
    private GameObject? _root;
    private RectTransform? _layout;
    private RectTransform? _overviewRoot;
    private readonly Dictionary<BppItemBoardId, Rect> _carpetBounds = new();

    // Desktop rows are always visible; Android publishes the scrolled state per frame.
    // Intersecting rows show their carpet (it peeks past the mask); complete rows also
    // show candidate tags, because an unmasked tag must not float over the footer.
    private readonly Dictionary<BppItemBoardId, bool> _rowVisible = new();
    private readonly Dictionary<BppItemBoardId, bool> _rowComplete = new();
#if BPP_ANDROID
    private readonly List<(TextMeshProUGUI Text, int Size)> _scaledLabels = new();
    private int _fontPercent = -1;
    private float _textScale = 1;
#endif
    private RectTransform? _markerRoot;
    private Vector2 _screenSize;
    private Rect _lastSafeArea;
    private int _layoutFrames = 2;
    private RectTransform? _supporterHost;

    // The left column scrolls as one unit on Android: row headers, dividers, the metric
    // strip and the preview regions all live under _boardContent. The native previews are
    // separate overlay canvases, so PublishRowBounds feeds them the scrolled screen rects.
    // The content is null on desktop, which keeps the desktop row parenting unchanged.
    private RectTransform? _boardContent = null;
    private float _railPanelLeft = float.NaN;
    private float _railPanelWidth = float.NaN;
#if BPP_ANDROID
    private RectTransform? _boardViewport;

    // Last content offset whose screen rects were published. Comparing it catches a
    // ScrollRect/LateUpdate clamp as well as the view's own drag and snap.
    private float _publishedScroll = float.NaN;
    private RectTransform? _supporterViewport;
    private RectTransform? _footerRoot;
    private Image? _footerShade;
    private readonly List<float> _boardSnapStops = new();
    private bool _boardPress;
    private bool _boardDragging;
    private Vector2 _boardPressPointer;
    private float _boardPressOffset;
#endif
    private RectTransform? _sidebar;
    private RectTransform? _corpusGroup;
    private RawImage? _backdrop;
    private Task<Texture2D?>? _backdropLoad;
    private BPPSupporterNativeAttributionRow? _supporters;
    private Vector2 _layoutSize;
    private LiveBuildPanelLayout _geometry;
    private TextMeshProUGUI? _title;
    private TextMeshProUGUI? _corpusTitle;
    private TextMeshProUGUI? _corpus;
    private TextMeshProUGUI? _corpusDetail;
    private TextMeshProUGUI? _navigationLabel;
    private TextMeshProUGUI? _pager;
    private RectTransform? _metrics;
    private readonly TextMeshProUGUI[] _metricLabels = new TextMeshProUGUI[
        LiveBuildPanelLayout.MetricCells
    ];
    private readonly TextMeshProUGUI[] _metricValues = new TextMeshProUGUI[
        LiveBuildPanelLayout.MetricCells
    ];
    private TextMeshProUGUI? _guidance;
    private TextMeshProUGUI? _hint;
    private Button? _refreshButton;
    private Button? _closeButton;
    private Button? _previousButton;
    private Button? _nextButton;
    private Image? _portrait;
    private EHero? _portraitHero;
    private Task<HeroPortraitLoadOutcome?>? _portraitLoad;
    private Task<Sprite?[]>? _skinLoad;
    private Sprite?[]? _skin;
    private float _retryAt;
    private LiveBuildPanelSnapshot? _snapshot;
    private readonly Vector3[] _corners = new Vector3[4];
    private bool _visible;
    private bool _disposed;

    internal LiveBuildPanelView(
        Transform parent,
        Action close,
        Action previous,
        Action next,
        Action refresh
    )
    {
        _parent = parent;
        _close = close;
        _previous = previous;
        _next = next;
        _refresh = refresh;
    }

    internal event Action<BppItemBoardId, Rect>? RowBoundsChanged;
    internal bool IsCreated => _root != null;
    internal int PresentationRevision { get; private set; }
#if BPP_ANDROID
    internal float BoardFooterPixels =>
        LiveBuildPanelLayout.ReadableBoardFooterHeight * CanvasScale;
#else
    internal float BoardFooterPixels => LiveBuildPanelLayout.BoardFooterHeight * CanvasScale;
#endif
    internal float BoardTopPixels => LiveBuildPanelLayout.BoardTopInset * CanvasScale;
    private float CanvasScale =>
#if BPP_ANDROID
        // Canvas.scaleFactor can transiently report 1 during panel hide/recreation. Native
        // previews remain in physical pixels then, so conversion must use our chosen baseline.
        AndroidOverlayScale.CanvasFactor(
            Screen.width,
            Screen.height,
            AndroidOverlayViewport.ScalePercent,
            matchHeight: true,
            applyPreference: false
        );
#else
        _root != null ? _root.GetComponent<Canvas>().scaleFactor : 1;
#endif
    private Transform OverviewParent => _overviewRoot != null ? _overviewRoot : _layout!;
    private Transform BoardParent => _boardContent != null ? _boardContent : _layout!;

    // The renderer parks a preview when its row does not intersect the board viewport:
    // a native preview canvas cannot be clipped by the uGUI mask, so a fully scrolled-out
    // row is moved off-screen instead of being left to draw over the panel. Unknown rows
    // are visible until the first layout publishes the real state.
    internal bool IsRowVisible(BppItemBoardId id) =>
        !_rowVisible.TryGetValue(id, out var visible) || visible;

    // Tags are only safe on a row wholly inside the viewport: their overlay canvas is not
    // masked, and a tag below a partial carpet would land in the footer.
    internal bool IsRowComplete(BppItemBoardId id) =>
        !_rowComplete.TryGetValue(id, out var complete) || complete;

#if BPP_ANDROID
    internal bool PointerReleasedThisFrame => Pointer.current?.press.wasReleasedThisFrame == true;

    /// <summary>Set while an inspection owns the screen; board drags are ignored.</summary>
    internal bool InputBlocked { get; set; }

    // A peeking preview extends below the viewport; a tap in the footer must not select a
    // card that the mask hides.
    internal bool BoardContainsPointer(Vector2 screenPoint) =>
        _boardViewport == null || BoardViewportContains(screenPoint);
#else
    internal bool BoardContainsPointer(Vector2 screenPoint) => true;
#endif

    internal void EnsureCreated()
    {
        if (_disposed || _root != null)
            return;
        if (NativeGameTypography.PrepareOwnedText(out _font) != NativeGameTypography.Outcome.Ready)
            return;
        _root = BppGameObjectFactory.Create(
            "LiveBuildPanelView",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster)
        );
        _root.transform.SetParent(_parent, false);
        var canvas = _root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = BppOverlaySorting.PanelUiToolkit;
        var scaler = _root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1600, 1000);
        // Four stacked carpets are height-constrained; wider displays must not
        // shrink their content rail relative to the text and header actions.
        scaler.matchWidthOrHeight = 1;
#if BPP_ANDROID
        AndroidOverlayViewport.ApplyCanvasScale(_root, matchHeight: true, applyPreference: false);
#endif

        // Full-screen backdrop and input wall preventing clicks from falling through on notch/cutout edges.
        var fullModalWall = Rect("FullModalWall", _root.transform, 0, 0, 1, 1);
        var fullModalImage = fullModalWall.gameObject.AddComponent<Image>();
        fullModalImage.color = new Color(.16f, .105f, .065f, 1);
        fullModalImage.raycastTarget = true;

        _layout = Rect("Panel", _root.transform, 0, 0, 1, 1);
        ApplySafeAreaInsets();
        _lastSafeArea = Screen.safeArea;
        _screenSize = new Vector2(Screen.width, Screen.height);

        // Panel-wide backdrop and the panel's modal input wall. A covering parent Image does not
        // starve the buttons under it: uGUI's RaycastAll returns every Graphic under the pointer,
        // parent and child alike, and ranks them by depth, so the child button still receives the
        // click. Clearing this raycastTarget is what breaks the panel: taps on empty panel area fall
        // through to the game UI underneath (the gem store opened that way on device), because
        // LiveBuildPanel has no CanvasGroup input gate. The backdrop must keep eating taps.
        var layoutBackground = _layout.gameObject.AddComponent<Image>();
        layoutBackground.color = new Color(.16f, .105f, .065f, 1);
        layoutBackground.raycastTarget = true;
        _backdrop = Rect("NativeBackdrop", _layout, 0, 0, 1, 1).gameObject.AddComponent<RawImage>();
        _backdrop.raycastTarget = false;
        _backdrop.color = Color.clear;
        _backdrop.gameObject.AddComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter
            .AspectMode
            .EnvelopeParent;
        var shade = Rect("BackgroundShade", _layout, 0, 0, 1, 1).gameObject.AddComponent<Image>();
        shade.color = new Color(.055f, .038f, .028f, .64f);
        shade.raycastTarget = false;
        var frame = Rect("PanelFrame", _layout, 0, 0, 1, 1);
        frame.offsetMin = new Vector2(7, 7);
        frame.offsetMax = new Vector2(-7, -7);
        Layer(frame, 0);
#if BPP_ANDROID
        _overviewRoot = Rect("OverviewChrome", _layout, 0, 0, 0, 0);
#endif
        _sidebar = Rect("Overview", OverviewParent, 0, 0, 0, 0);
        var sidebarRule = Rect("SidebarRule", _sidebar, 0, 0, 0, 1);
        sidebarRule.sizeDelta = new Vector2(1, 0);
        var sidebarRuleImage = sidebarRule.gameObject.AddComponent<Image>();
        sidebarRuleImage.color = new Color(.58f, .43f, .20f, .35f);
        sidebarRuleImage.raycastTarget = false;
        foreach (var dividerY in new[] { .11f, .70f })
        {
            var divider = Rect("SectionDivider", _sidebar, .08f, dividerY, .84f, 0);
            divider.sizeDelta = new Vector2(0, 1);
            divider.gameObject.AddComponent<Image>().color = new Color(.58f, .43f, .20f, .5f);
        }
        _title = Text(OverviewParent, string.Empty, .02f, .008f, .49f, .05f, 32);
        _title.textWrappingMode = TextWrappingModes.Normal;
#if BPP_ANDROID
        // At large UI sizes the complete attribution no longer fits beside four boards. Keep
        // its text size and scroll it independently, without moving the close/refresh/navigation.
        _supporterViewport = Rect("SupporterViewport", OverviewParent, 0, 0, 0, 0);
        _supporterViewport.gameObject.AddComponent<RectMask2D>();
        var supporterHitArea = _supporterViewport.gameObject.AddComponent<Image>();
        supporterHitArea.color = Color.clear;
        _supporterHost = Rect("SupporterAttribution", _supporterViewport, 0, 0, 1, 0);
        _supporterHost.pivot = new Vector2(0, 1);
        _supporterHost.anchoredPosition = Vector2.zero;
        _supporterHost.sizeDelta = new Vector2(0, 240);
        var supporterScroll = _supporterViewport.gameObject.AddComponent<ScrollRect>();
        supporterScroll.viewport = _supporterViewport;
        supporterScroll.content = _supporterHost;
        supporterScroll.horizontal = false;
        supporterScroll.vertical = true;
        supporterScroll.movementType = ScrollRect.MovementType.Clamped;
#else
        _supporterHost = Rect("SupporterAttribution", _layout, .02f, .048f, .65f, .027f);
#endif
        _supporters = new BPPSupporterNativeAttributionRow(
            _supporterHost,
            _font!,
            TextAnchor.UpperLeft,
            stacked: true
        );
        _corpusGroup = Rect("BuildLibrary", OverviewParent, 0, 0, 0, 0);
        _corpusTitle = Text(_corpusGroup, "", 0, 0, 0, 0, 15);
        _corpus = Text(_corpusGroup, "", 0, 0, 0, 0, 14, Muted);
        _corpus.textWrappingMode = TextWrappingModes.Normal;
        _refreshButton = Button(_corpusGroup, "", 0, 0, 0, 0, _refresh, size: 14);
        _closeButton = Button(OverviewParent, "", .943f, .027f, .044f, .045f, _close);
#if BPP_ANDROID
        // The four boards keep a minimum height and scroll inside this viewport. The mask
        // clips the row chrome; native previews are parked by the renderer when a row is not
        // wholly visible because their separate overlay canvases cannot be stencil-masked.
        _boardViewport = Rect("BoardViewport", _layout, 0, 0, 0, 0);
        _boardViewport.gameObject.AddComponent<RectMask2D>();
        _boardContent = Rect("BoardContent", _boardViewport, 0, 0, 1, 0);
        _boardContent.anchorMin = new Vector2(0, 1);
        _boardContent.anchorMax = new Vector2(1, 1);
        _boardContent.pivot = new Vector2(.5f, 1);
        _boardContent.anchoredPosition = Vector2.zero;
        _boardContent.sizeDelta = new Vector2(0, 1000);
        var boardScroll = _boardViewport.gameObject.AddComponent<ScrollRect>();
        boardScroll.viewport = _boardViewport;
        boardScroll.content = _boardContent;
        boardScroll.horizontal = false;
        boardScroll.vertical = true;
        boardScroll.movementType = ScrollRect.MovementType.Clamped;
        // Touch is driven by LiveBuildPanelView.TickBoardScroll: a native preview card
        // would otherwise eat the drag before uGUI's ScrollRect sees it.
        boardScroll.inertia = false;
#endif
        // Four peers always share the same board dimensions. Native carpet aspect and
        // ten sockets determine card scale; sparse sources never grow individual cards.
        var ids = new[]
        {
            BppItemBoardId.FinalBuild,
            BppItemBoardId.LiveShop,
            BppItemBoardId.LiveStash,
            BppItemBoardId.LiveBoard,
        };
        for (var i = 0; i < ids.Length; i++)
        {
            var y = .103f + i * .205f + (i > 0 ? .033f : 0);
            var row = new Row { Index = i };
            row.Divider = Rect("RowDivider", BoardParent, 0, 0, 0, 0);
            var divider = row.Divider.gameObject.AddComponent<Image>();
            divider.color =
                i == 0 ? new Color(.72f, .54f, .24f, .8f) : new Color(.58f, .43f, .20f, .3f);
            divider.raycastTarget = false;
            row.Header = Rect("RowHeader", BoardParent, .02f, y, .96f, .024f);
            row.Title = Text(row.Header, "", 0, 0, .50f, 1, 18);
            row.Region = Rect(ids[i].ToString(), BoardParent, .02f, y + .033f, .96f, .17f);
            row.Status = Text(row.Header, "", .4f, 0, .60f, 1, 14, Muted);
            row.Status.alignment = TextAlignmentOptions.MidlineRight;
            _rows.Add(ids[i], row);
        }
        var recommendationHeader = _rows[BppItemBoardId.FinalBuild].Header;
        _portrait = Rect("HeroPortrait", recommendationHeader, 0, 0, .03f, 1)
            .gameObject.AddComponent<Image>();
        _portrait.preserveAspect = true;
        _portrait.raycastTarget = false;
        _portrait.color = Color.clear;
        _navigationLabel = Text(OverviewParent, "", 0, 0, 0, 0, 17);
        _previousButton = NavigationButton(_previous, forward: false);
        _pager = Text(OverviewParent, "", 0, 0, 0, 0, 17, Ink, true);
        _nextButton = NavigationButton(_next, forward: true);
        // One fixed cell per metric: a run-on line silently ellipsized the last one away.
        _metrics = Rect("MatchMetrics", BoardParent, 0, 0, 0, 0);
        for (var cell = 0; cell < LiveBuildPanelLayout.MetricCells; cell++)
        {
            var slot = Rect(
                "Metric",
                _metrics,
                cell / (float)LiveBuildPanelLayout.MetricCells,
                0,
                1f / LiveBuildPanelLayout.MetricCells,
                1
            );
            _metricLabels[cell] = Text(slot, "", 0, 0, 1, .42f, 11, Muted);
            _metricValues[cell] = Text(slot, "", 0, .42f, 1, .58f, 16);
        }
        _guidance = Text(BoardParent, "", 0, 0, 0, 0, 14, Muted);
#if BPP_ANDROID
        // A peeking carpet extends past the scroll mask into the footer; the native preview
        // sorts above the panel, so the hint needs its own canvas above the preview to stay
        // readable. The hint never moves horizontally, so a full-panel host keeps Place()'s
        // panel coordinates intact.
        _footerRoot = Rect("FooterChrome", _layout, 0, 0, 1, 1);
        var footerCanvas = _footerRoot.gameObject.AddComponent<Canvas>();
        footerCanvas.overrideSorting = true;
        footerCanvas.sortingOrder = BppOverlaySorting.PanelForeground;
        // The shade gives the hint a readable base where a peeking carpet would otherwise
        // show through, and it is also the clean bottom edge of the board column.
        _footerShade = Rect("FooterShade", _footerRoot, 0, 0, 1, 0)
            .gameObject.AddComponent<Image>();
        _footerShade.color = new Color(.10f, .07f, .04f, .94f);
        _footerShade.raycastTarget = false;
        _hint = Text(_footerRoot, "", .02f, .969f, .96f, .022f, 12, Muted, true);
#else
        _hint = Text(_layout, "", .02f, .969f, .96f, .022f, 12, Muted, true);
#endif
        _markerRoot = Rect("CandidateMarkers", _root.transform, 0, 0, 1, 1);
        var foreground = _markerRoot.gameObject.AddComponent<Canvas>();
        foreground.overrideSorting = true;
        foreground.sortingOrder = BppOverlaySorting.PanelForeground;
        var detail = Rect("CorpusDetail", _markerRoot, .48f, .091f, .49f, .10f);
        var detailBackground = detail.gameObject.AddComponent<Image>();
        detailBackground.color = new Color(.10f, .065f, .02f, .99f);
        detailBackground.raycastTarget = false;
        _corpusDetail = Text(detail, "", 0, 0, 1, 1, 17);
        _corpusDetail.textWrappingMode = TextWrappingModes.Normal;
        _corpusDetail.overflowMode = TextOverflowModes.Overflow;
        _corpusDetail.margin = new Vector4(12, 7, 12, 7);
        _corpusDetail.transform.parent.gameObject.SetActive(false);
        _root.SetActive(_visible);
        if (_snapshot != null)
            Refresh(_snapshot);
    }

    internal void SetVisible(bool visible)
    {
        _visible = visible;
        EnsureCreated();
        if (_root != null)
            _root.SetActive(visible);
        if (!visible)
        {
            ClearMarkers();
#if BPP_ANDROID
            _boardPress = false;
            _boardDragging = false;
#endif
        }
    }

    internal void Refresh(LiveBuildPanelSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (_root == null)
            return;
        _title!.text = LiveBuildPanelText.Title();
        _corpusTitle!.text = LiveBuildPanelText.CorpusLibrary();
        _navigationLabel!.text = LiveBuildPanelText.BrowseBuilds();
        _hint!.text = LiveBuildPanelText.SelectionHint();
        SetButtonText(_refreshButton!, snapshot.FinalBuildRefreshButtonText);
        _refreshButton!.interactable = snapshot.FinalBuildRefreshButtonEnabled;
        SetButtonText(_closeButton!, LiveBuildPanelText.Close());
        _supporters!.Bind(snapshot.Supporters, LiveBuildPanelText.Subtitle());
        _corpus!.text =
            snapshot.CorpusState == LiveBuildCorpusState.Summary
                ? snapshot.CorpusFreshnessText
                : snapshot.CorpusStatusText;
        _corpus.color = StatusColor(
            snapshot.CorpusState == LiveBuildCorpusState.Summary
                ? snapshot.CorpusFreshnessSeverity
                : snapshot.CorpusStatusSeverity
        );
        _corpusDetail!.text = string.Join(
            "\n",
            new[]
            {
                snapshot.CorpusStatusText,
                snapshot.CorpusFreshnessText,
                snapshot.CorpusStatusTooltip,
            }.Where(value => !string.IsNullOrWhiteSpace(value))
        );
        _pager!.text =
            snapshot.RecommendationCount > 0
                ? $"{snapshot.RecommendationIndex + 1} / {snapshot.RecommendationCount}"
                : "0 / 0";
        _previousButton!.interactable = _nextButton!.interactable =
            snapshot.RecommendationCount > 1;
        var hasRecommendation = snapshot.MatchesState == LiveBuildMatchesState.HasRecommendation;
        _metrics!.gameObject.SetActive(hasRecommendation);
        _guidance!.gameObject.SetActive(!hasRecommendation);
        _guidance.text = hasRecommendation ? string.Empty : snapshot.MatchesGuidance;
        if (hasRecommendation)
        {
            SetMetric(
                0,
                LiveBuildPanelText.MatchRateLabel(),
                LiveBuildPanelText.MatchRateValue(snapshot.MatchTenWinRateBps)
            );
            SetMetric(
                1,
                LiveBuildPanelText.MatchSampleLabel(),
                LiveBuildPanelText.MatchSampleValue(snapshot.MatchTenWinRunCount)
            );
            SetMetric(
                2,
                LiveBuildPanelText.MatchFinalDayLabel(),
                LiveBuildPanelText.MatchFinalDayValue(snapshot.MatchP75FinalDay)
            );
            SetMetric(
                3,
                LiveBuildPanelText.MatchMatchedLabel(),
                LiveBuildPanelText.MatchMatchedValue(
                    snapshot.MatchMatchedCardCount,
                    snapshot.CandidateTemplateIds.Count
                )
            );
        }
        foreach (var row in snapshot.Rows)
        {
            var ui = _rows[row.Board.Id];
            ui.Title.text = row.Title;
            ui.MarkerText = row.CanToggleCandidates
                ? LiveBuildPanelText.CandidateTag()
                : LiveBuildPanelText.MatchTag();
            ui.Status.text =
                ui.Message.Length > 0 ? ui.Message
                : row.Board.Cards.Count == 0 ? row.EmptyText
                : string.Empty;
        }
        if (_portraitHero != snapshot.Hero)
        {
            _portraitHero = snapshot.Hero;
            _portrait!.color = Color.clear;
            _portraitLoad = snapshot.Hero is { } hero
                ? HeroPortraitSpriteProvider.LoadDefaultPortraitAsync(hero)
                : null;
        }
    }

    internal void Tick()
    {
        if (!_visible || _disposed)
            return;
        EnsureCreated();
        if (_root == null)
            return;
        if (
            Time.unscaledTime >= _retryAt
            && Services.TryGet<AssetLoader>(out var loader)
            && loader != null
        )
        {
            _retryAt = Time.unscaledTime + 5;
            if ((_skin == null || _skin.Any(sprite => sprite == null)) && _skinLoad == null)
                _skinLoad = Task.WhenAll(
                    Addresses.Select(address =>
                        NativeGlobalAssetLoader.LoadByAddressAsync<Sprite>(loader, address)
                    )
                );
            if (_backdrop!.texture == null && _backdropLoad == null)
                _backdropLoad = NativeGlobalAssetLoader.LoadByAddressAsync<Texture2D>(
                    loader,
                    "Collections_Background_01_TUI"
                );
        }
        if (_backdropLoad?.IsCompleted == true)
        {
            if (
                _backdropLoad.Status == TaskStatus.RanToCompletion
                && _backdropLoad.Result is { } texture
            )
            {
                _backdrop!.texture = texture;
                _backdrop.color = Color.white;
                _backdrop.GetComponent<AspectRatioFitter>().aspectRatio =
                    (float)texture.width / texture.height;
            }
            else
                _ = _backdropLoad.Exception;
            _backdropLoad = null;
        }
        if (_skinLoad?.IsCompleted == true)
        {
            if (_skinLoad.Status == TaskStatus.RanToCompletion)
            {
                _skin = _skinLoad.Result;
                foreach (var art in _art)
                    ApplyArt(art.Image, art.Index);
                _supporters!.SetSkin(_skin?[0], _skin?[1]);
            }
            else
                _ = _skinLoad.Exception;
            _skinLoad = null;
        }
        if (_portraitLoad?.IsCompleted == true)
        {
            if (
                _portraitLoad.Status == TaskStatus.RanToCompletion
                && _portraitLoad.Result?.Sprite is { } sprite
            )
            {
                _portrait!.sprite = sprite;
                _portrait.color = Color.white;
            }
            else
                _ = _portraitLoad.Exception;
            _portraitLoad = null;
        }
        var screen = new Vector2(Screen.width, Screen.height);
        var safeArea = Screen.safeArea;
#if BPP_ANDROID
        if (
            _root != null
            && AndroidOverlayViewport.ApplyCanvasScale(
                _root,
                matchHeight: true,
                applyPreference: false
            )
        )
        {
            // Screen-pixel rail bounds are stale even when the canvas-unit layout did not
            // change. Force the renderer to re-read the carpet and re-fit its pixel regions.
            _carpetBounds.Clear();
            _layoutFrames = 2;
            PresentationRevision++;
        }
        if (_fontPercent != AndroidOverlayViewport.ScalePercent)
            _layoutFrames = 2;
        TickBoardScroll();
#endif
        var viewportChanged = ApplySafeAreaInsets();
        if (_screenSize != screen || _lastSafeArea != safeArea || viewportChanged)
        {
            _screenSize = screen;
            _lastSafeArea = safeArea;
            _layoutFrames = 2;
        }
        var relaidOut = _layoutFrames > 0;
        if (relaidOut)
        {
            _layoutFrames--;
            Canvas.ForceUpdateCanvases();
            UpdateLayout();
        }
#if BPP_ANDROID
        var scrollOffset = _boardContent?.anchoredPosition.y ?? 0;
        if (relaidOut || !Mathf.Approximately(scrollOffset, _publishedScroll))
        {
            _publishedScroll = scrollOffset;
            PublishRowBounds();
        }
#else
        if (relaidOut)
            PublishRowBounds();
#endif
        _corpusDetail!.transform.parent.gameObject.SetActive(
            Pointer.current != null
                && RectTransformUtility.RectangleContainsScreenPoint(
                    _corpus!.rectTransform,
                    Pointer.current.position.ReadValue()
                )
        );
    }

    // Publish the row rects in screen pixels: the native preview canvases live outside the
    // scrolling panel canvas, so they need the scrolled world rect, not the content-local one.
    private void PublishRowBounds()
    {
#if BPP_ANDROID
        var viewport = default(Rect);
        if (_boardViewport != null)
        {
            _boardViewport.GetWorldCornersManaged(_corners);
            viewport = UnityEngine.Rect.MinMaxRect(
                _corners[0].x,
                _corners[0].y,
                _corners[2].x,
                _corners[2].y
            );
        }
#endif
        foreach (var pair in _rows)
        {
            var row = pair.Value;
            row.Region.GetWorldCornersManaged(_corners);
            var bounds = UnityEngine.Rect.MinMaxRect(
                _corners[0].x,
                _corners[0].y,
                _corners[2].x,
                _corners[2].y
            );
            if (bounds.width <= 1 || bounds.height <= 1)
                continue;
#if BPP_ANDROID
            // Visibility follows the carpet region, not the header above it: a header can peek
            // in while the board is still below the mask, and tags belong to the board alone.
            // Update before the event so SetBounds parks or restores the right preview.
            _rowVisible[pair.Key] = IsRowIntersecting(bounds.yMax, bounds.yMin, viewport);
            _rowComplete[pair.Key] = IsRowFullyInside(bounds.yMax, bounds.yMin, viewport);
#endif
            if (bounds != row.Bounds)
            {
                row.Bounds = bounds;
                RowBoundsChanged?.Invoke(pair.Key, bounds);
            }
        }
    }

#if BPP_ANDROID
    private static bool IsRowIntersecting(float rowTop, float rowBottom, Rect viewport)
    {
        if (viewport.height <= 0)
            return true;
        const float tolerance = 1.5f;
        return rowBottom <= viewport.yMax + tolerance && rowTop >= viewport.yMin - tolerance;
    }

    private static bool IsRowFullyInside(float rowTop, float rowBottom, Rect viewport)
    {
        if (viewport.height <= 0)
            return true;
        const float tolerance = 1.5f;
        if (rowTop - rowBottom <= viewport.height)
            return rowTop <= viewport.yMax + tolerance && rowBottom >= viewport.yMin - tolerance;
        // A row taller than the viewport can never be wholly visible; keep its tags reachable
        // by treating it as complete when it covers the viewport.
        return rowTop >= viewport.yMax - tolerance && rowBottom <= viewport.yMin + tolerance;
    }

    private void BuildBoardSnapStops()
    {
        _boardSnapStops.Clear();
        var max = _geometry.BoardMaxScroll;
        if (max <= .5f)
            return;
        // 0 keeps the initial (unscrolled) position stable for taps; the row tops make each
        // full row land at the viewport top; max reveals the last row against the footer.
        _boardSnapStops.Add(0);
        foreach (var row in _rows.Values)
            _boardSnapStops.Add(Mathf.Clamp(_geometry.TitleTop(row.Index) - 8, 0, max));
        _boardSnapStops.Add(max);
        _boardSnapStops.Sort();
    }

    private float NearestBoardStop(float offset)
    {
        var best = offset;
        var distance = float.MaxValue;
        foreach (var stop in _boardSnapStops)
        {
            var candidate = Mathf.Abs(stop - offset);
            if (candidate >= distance)
                continue;
            distance = candidate;
            best = stop;
        }
        return best;
    }

    private bool BoardViewportContains(Vector2 screenPoint)
    {
        if (
            _boardViewport == null
            || !RectTransformUtility.RectangleContainsScreenPoint(_boardViewport, screenPoint, null)
        )
            return false;
        // The viewport is the whole left column; the four peer carpets share one narrower
        // rail. Starting a drag on the overview's left edge must not scroll the boards.
        if (!_carpetBounds.TryGetValue(BppItemBoardId.FinalBuild, out var carpet))
            return true;
        return screenPoint.x >= carpet.xMin - 1 && screenPoint.x <= carpet.xMax + 1;
    }

    // Touch drags are read from the pointer directly: a native preview card sits in its own
    // overlay canvas above this one and would consume uGUI drag events before the ScrollRect.
    private void TickBoardScroll()
    {
        if (_boardContent == null || _geometry.Width <= 0)
            return;
        if (InputBlocked)
        {
            _boardPress = false;
            _boardDragging = false;
            return;
        }
        var pointer = Pointer.current;
        var position = pointer?.position.ReadValue();
        var pressed = pointer?.press.isPressed == true;
        var before = _boardContent.anchoredPosition.y;
        if (
            pointer != null
            && pointer.press.wasPressedThisFrame
            && position is { } press
            && BoardViewportContains(press)
        )
        {
            _boardPress = true;
            _boardDragging = false;
            _boardPressPointer = press;
            _boardPressOffset = before;
        }
        if (_boardPress)
        {
            if (pressed && position is { } current)
            {
                if (
                    !_boardDragging
                    && Mathf.Abs(current.y - _boardPressPointer.y)
                        > LiveBuildPanelLayout.BoardDragThresholdPixels
                )
                    _boardDragging = true;
                if (_boardDragging)
                {
                    var scale = Mathf.Max(.001f, CanvasScale);
                    var offset = Mathf.Clamp(
                        _boardPressOffset + (current.y - _boardPressPointer.y) / scale,
                        0,
                        _geometry.BoardMaxScroll
                    );
                    _boardContent.anchoredPosition = new Vector2(0, offset);
                }
            }
            else
            {
                _boardPress = false;
                _boardDragging = false;
            }
        }
        if (!pressed && !_boardPress && _geometry.BoardMaxScroll > .5f)
        {
            var offset = _boardContent.anchoredPosition.y;
            var target = NearestBoardStop(offset);
            if (Mathf.Abs(offset - target) > .25f)
                _boardContent.anchoredPosition = new Vector2(
                    0,
                    Mathf.MoveTowards(offset, target, Time.unscaledDeltaTime * 2600f)
                );
        }
    }
#endif

    internal void SetBoardStatus(
        BppItemBoardId id,
        NativeMonsterBoardStatus status,
        int unavailableCount = 0
    )
    {
        if (!_rows.TryGetValue(id, out var row))
            return;
        row.Message = status switch
        {
            NativeMonsterBoardStatus.Loading => LiveBuildPanelText.LoadingBoard(),
            NativeMonsterBoardStatus.Failed => LiveBuildPanelText.BoardFailed(),
            NativeMonsterBoardStatus.Partial => L.Resolve(
                new LocalizedTextSet(
                    $"{unavailableCount} cards are unavailable in this game version.",
                    $"{unavailableCount} 张卡牌在当前版本不可用。"
                )
            ),
            _ => string.Empty,
        };
        if (_snapshot != null)
            Refresh(_snapshot);
    }

    private void UpdateLayout()
    {
        var size = _layout!.rect.size;
        var presentationChanged = false;
#if BPP_ANDROID
        presentationChanged = _fontPercent != AndroidOverlayViewport.ScalePercent;
#endif
        if (size.x <= 1 || size.y <= 1 || (size == _layoutSize && !presentationChanged))
            return;
        if (size != _layoutSize)
            _carpetBounds.Clear();
        _layoutSize = size;
#if BPP_ANDROID
        _fontPercent = AndroidOverlayViewport.ScalePercent;
        _textScale = AndroidOverlayViewport.UiScale;
        foreach (var entry in _scaledLabels)
            ApplyTextScale(entry.Text, entry.Size);
        _geometry = new LiveBuildPanelLayout(
            size.x,
            size.y,
            ReadableChrome: true,
            ChromeScale: _textScale
        );
        var scrollOffset = _boardContent != null ? _boardContent.anchoredPosition.y : 0;
        Place(
            _boardViewport!,
            _geometry.BoardLeft,
            _geometry.BoardViewportTop,
            _geometry.BoardWidth,
            _geometry.BoardViewportHeight
        );
        _boardContent!.sizeDelta = new Vector2(0, _geometry.BoardContentHeight);
        _boardContent.anchoredPosition = new Vector2(
            0,
            Mathf.Clamp(scrollOffset, 0, _geometry.BoardMaxScroll)
        );
        BuildBoardSnapStops();
#else
        _geometry = new LiveBuildPanelLayout(size.x, size.y);
#endif
        PresentationRevision++;
        _railPanelLeft = float.NaN;
        _railPanelWidth = float.NaN;
        var boardLeft = _boardContent != null ? 0f : _geometry.BoardLeft;
        foreach (var pair in _rows)
        {
            var row = pair.Value;
            Place(
                row.Region,
                boardLeft,
                _geometry.BoardTop(row.Index),
                _geometry.BoardWidth,
                _geometry.BoardHeight
            );
            if (_carpetBounds.TryGetValue(pair.Key, out var carpet))
                SetBoardRail(pair.Key, carpet);
            else
                ApplyBoardRail(row, boardLeft, _geometry.BoardWidth);
        }
        var overviewLeft = _geometry.BoardLeft;
        var overviewWidth = _geometry.BoardWidth;
        if (_carpetBounds.TryGetValue(BppItemBoardId.FinalBuild, out var recommendationCarpet))
        {
            overviewLeft = PanelLeftOf(recommendationCarpet);
            overviewWidth = recommendationCarpet.width / CanvasScale;
        }
        ApplyHeaderRail(overviewLeft, overviewWidth);
    }

    // Screen pixels -> the board parent's top-left-relative units. On Android the parent is
    // the scrolling content, so the same value also positions the row chrome under the mask.
    private float LocalLeftOf(Rect screenBounds)
    {
        var parent = _boardContent != null ? _boardContent : _layout!;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            parent,
            screenBounds.min,
            null,
            out var local
        );
        return local.x - parent.rect.xMin;
    }

    // Screen pixels -> panel-left-relative units. The scrolling content starts at BoardLeft,
    // so its local rail has to be shifted back to the panel frame the overview lives in.
    private float PanelLeftOf(Rect screenBounds) =>
        LocalLeftOf(screenBounds) + (_boardContent != null ? _geometry.BoardLeft : 0);

    internal void SetBoardRail(BppItemBoardId id, Rect screenBounds)
    {
        if (_layout == null || screenBounds.width <= 1)
            return;
        _carpetBounds[id] = screenBounds;
        // Only text follows the loaded carpet. Feeding these bounds back into the
        // preview region would shrink the board again on each layout pass.
        var width = screenBounds.width / CanvasScale;
        ApplyBoardRail(_rows[id], LocalLeftOf(screenBounds), width);
        if (id != BppItemBoardId.FinalBuild)
            return;
        var panelLeft = PanelLeftOf(screenBounds);
        if (
            float.IsFinite(_railPanelLeft)
            && Mathf.Abs(panelLeft - _railPanelLeft) <= .5f
            && Mathf.Abs(width - _railPanelWidth) <= .5f
        )
            return;
        _railPanelLeft = panelLeft;
        _railPanelWidth = width;
        ApplyHeaderRail(panelLeft, width);
    }

    private void ApplyBoardRail(Row row, float left, float width)
    {
        Place(row.Divider, left, _geometry.TitleTop(row.Index) - 8, width, row.Index == 0 ? 2 : 1);
        Place(row.Header, left, _geometry.TitleTop(row.Index), width, _geometry.HeaderRowHeight);
#if BPP_ANDROID
        var portrait = Mathf.Min(24 * _textScale, Mathf.Max(1, _geometry.HeaderRowHeight - 2));
        var titleLeft = row.Index == 0 ? portrait + 6 : 0;
#else
        const float portrait = 24;
        var titleLeft = row.Index == 0 ? 30 : 0;
#endif
        var titleWidth = Mathf.Min(230, width * .38f);
        Place(row.Title.rectTransform, titleLeft, 0, titleWidth, _geometry.HeaderRowHeight);
        var statusLeft = titleLeft + titleWidth + 10;
        Place(
            row.Status.rectTransform,
            statusLeft,
            0,
            Mathf.Max(1, width - statusLeft),
            _geometry.HeaderRowHeight
        );
        if (row.Index != 0)
            return;
        Place(_portrait!.rectTransform, 0, 2, portrait, portrait);
        Place(_metrics!, left, _geometry.MetricsTop, width, _geometry.MetricRowHeight);
        Place(
            _guidance!.rectTransform,
            left,
            _geometry.MetricsTop,
            width,
            _geometry.MetricRowHeight
        );
    }

    private void ApplyHeaderRail(float left, float width)
    {
        var sidebarLeft = _geometry.SidebarLeft(left, width);
        var contentLeft = sidebarLeft + 24;
        var contentWidth = LiveBuildPanelLayout.SidebarWidth - 48;
        var overviewGeometry = _geometry;
        var overviewLeft = sidebarLeft;
#if BPP_ANDROID
        // The board rail reserves ReadableSidebarScaleMax; the overview must not grow past
        // that reservation or it would cover the carpet (or leave the panel).
        var overviewScale = Mathf.Min(_textScale, LiveBuildPanelLayout.ReadableSidebarScaleMax);
        overviewGeometry = new LiveBuildPanelLayout(
            LiveBuildPanelLayout.SidebarWidth,
            _geometry.Height / overviewScale
        );
        Place(
            _overviewRoot!,
            sidebarLeft,
            0,
            LiveBuildPanelLayout.SidebarWidth,
            overviewGeometry.Height
        );
        _overviewRoot!.localScale = new Vector3(overviewScale, overviewScale, 1);
        contentLeft = 24;
        overviewLeft = 0;
#endif
        Place(
            _sidebar!,
            overviewLeft,
            20,
            LiveBuildPanelLayout.SidebarWidth,
            overviewGeometry.Height - 40
        );
        var compact = overviewGeometry.Height < 800;
        Place(_title!.rectTransform, contentLeft, compact ? 24 : 42, contentWidth - 80, 64);
        Place(
            _closeButton!.transform.RequireRectTransform(),
            contentLeft + contentWidth - 68,
            compact ? 40 : 58,
            68,
            32
        );
        Place(_corpusGroup!, contentLeft, overviewGeometry.CorpusTop, contentWidth, 128);
        Place(_corpusTitle!.rectTransform, 0, 14, contentWidth - 108, 28);
        Place(_refreshButton!.transform.RequireRectTransform(), contentWidth - 94, 14, 94, 28);
        Place(_corpus!.rectTransform, 0, 56, contentWidth, 60);
        _corpus.alignment = TextAlignmentOptions.TopLeft;
        Place(
            _navigationLabel!.rectTransform,
            contentLeft,
            overviewGeometry.NavigationTop,
            contentWidth - 116,
            28
        );
        Place(
            _pager!.rectTransform,
            contentLeft + contentWidth - 104,
            overviewGeometry.NavigationTop,
            104,
            28
        );
        _pager.alignment = TextAlignmentOptions.MidlineRight;
        var navigationButtonWidth = (contentWidth - 12) / 2;
        Place(
            _previousButton!.transform.RequireRectTransform(),
            contentLeft,
            overviewGeometry.NavigationTop + 44,
            navigationButtonWidth,
            56
        );
        Place(
            _nextButton!.transform.RequireRectTransform(),
            contentLeft + navigationButtonWidth + 12,
            overviewGeometry.NavigationTop + 44,
            navigationButtonWidth,
            56
        );
#if BPP_ANDROID
        Place(
            _supporterViewport!,
            contentLeft,
            overviewGeometry.SupportersTop,
            contentWidth,
            overviewGeometry.SupportersViewportHeight
        );
        // Do not reset anchoredPosition: it is the user's current scroll offset.
        _supporterHost!.sizeDelta = new Vector2(0, 240);
#else
        Place(_supporterHost!, contentLeft, _geometry.SupportersTop, contentWidth, 240);
#endif
        // Anchored under the line it explains, not at a fixed offset over the boards.
        var detail = _corpusDetail!.transform.parent.RequireRectTransform();
#if BPP_ANDROID
        // Detail owns a foreground canvas outside OverviewChrome; convert the local overview
        // point through screen pixels so safe-area offsets and the chrome scale apply once.
        var screen = RectTransformUtility.WorldToScreenPoint(
            null,
            _overviewRoot!.TransformPoint(
                new Vector3(contentLeft, -(overviewGeometry.CorpusTop + 134), 0)
            )
        );
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            _markerRoot!,
            screen,
            null,
            out var local
        );
        detail.anchorMin = detail.anchorMax = new Vector2(.5f, .5f);
        detail.pivot = new Vector2(0, 1);
        detail.anchoredPosition = local;
        detail.sizeDelta = new Vector2(contentWidth * _textScale, 110 * _textScale);
#else
        Place(detail, contentLeft, _geometry.CorpusTop + 134, contentWidth, 110);
#endif
#if BPP_ANDROID
        Place(
            _footerShade!.rectTransform,
            left,
            _geometry.FooterTop,
            width,
            _geometry.ChromeFooterHeight
        );
#endif
        Place(_hint!.rectTransform, left, _geometry.FooterTop, width, _geometry.ChromeFooterHeight);
    }

    private static void Place(RectTransform rect, float left, float top, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
        rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(left, -top);
        rect.sizeDelta = new Vector2(Mathf.Max(1, width), height);
    }

    internal void BeginMarkers(BppItemBoardId id) => _rows[id].VisibleMarkers = 0;

    internal void AddMarker(BppItemBoardId id, Rect bounds, bool selectable)
    {
        if (_markerRoot == null)
            return;
        var row = _rows[id];
        var index = row.VisibleMarkers++;
        if (index == row.Markers.Count)
        {
            var marker = Rect("CandidateTag", _markerRoot, 0, 0, 0, 0);
            marker.anchorMin = marker.anchorMax = new Vector2(.5f, .5f);
            marker.pivot = new Vector2(.5f, 1);
            Layer(marker, selectable ? 2 : 1);
            Layer(marker, 0);
            Text(marker, string.Empty, .04f, .02f, .92f, .96f, 12, Ink, true);
            row.Markers.Add(marker);
        }
        var target = row.Markers[index];
        target.GetComponentInChildren<TextMeshProUGUI>(true).text = row.MarkerText;
        var scale = _root!.GetComponent<Canvas>().scaleFactor;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            _markerRoot,
            new Vector2(bounds.center.x, bounds.yMin - 1 * scale),
            null,
            out var local
        );
        target.anchoredPosition = local;
        target.sizeDelta = new Vector2(
            Mathf.Clamp(bounds.width / scale * .68f, 42, 72),
#if BPP_ANDROID
            LiveBuildPanelLayout.ReadableBadgeHeight
#else
            LiveBuildPanelLayout.BadgeHeight
#endif
        );
        target.gameObject.SetActive(true);
    }

    internal void EndMarkers(BppItemBoardId id)
    {
        var row = _rows[id];
        for (var i = row.VisibleMarkers; i < row.Markers.Count; i++)
            row.Markers[i].gameObject.SetActive(false);
    }

    private void ClearMarkers()
    {
        foreach (var row in _rows.Values)
        foreach (var marker in row.Markers)
            marker.gameObject.SetActive(false);
    }

    private static Color StatusColor(LiveBuildRefreshSeverity severity) =>
        severity switch
        {
            LiveBuildRefreshSeverity.Failure => new Color(.92f, .62f, .34f),
            LiveBuildRefreshSeverity.Success => new Color(.62f, .8f, .54f),
            LiveBuildRefreshSeverity.Pending => new Color(.59f, .76f, .89f),
            _ => Muted,
        };

    private void SetMetric(int cell, string label, string value)
    {
        _metricLabels[cell].text = label;
        _metricValues[cell].text = value;
    }

    private static void SetButtonText(Button button, string value) =>
        button.GetComponentInChildren<TextMeshProUGUI>(true).text = value;

    private static RectTransform Rect(
        string name,
        Transform parent,
        float x,
        float y,
        float width,
        float height
    )
    {
        var rect = BppGameObjectFactory
            .Create(name, typeof(RectTransform))
            .GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = new Vector2(x, 1 - y - height);
        rect.anchorMax = new Vector2(x + width, 1 - y);
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        return rect;
    }

    private Image Layer(Transform parent, int index)
    {
        var image = Rect("NativeArt", parent, 0, 0, 1, 1).gameObject.AddComponent<Image>();
        image.type = Image.Type.Sliced;
        image.pixelsPerUnitMultiplier = 2.5f;
        image.raycastTarget = false;
        _art.Add((image, index));
        ApplyArt(image, index);
        return image;
    }

    private void ApplyArt(Image image, int index)
    {
        image.sprite = _skin?[index];
        image.color = image.sprite != null ? Color.white : Color.clear;
    }

    private Button Button(
        Transform parent,
        string? title,
        float x,
        float y,
        float width,
        float height,
        Action click,
        int size = 17
    )
    {
        var rect = Rect("Action", parent, x, y, width, height);
        var background = Layer(rect, 1);
        background.raycastTarget = true;
        Layer(rect, 0);
        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = background;
        button.onClick.AddListener(() => click());
        if (title != null)
            Text(rect, title, .03f, .04f, .94f, .92f, size, Ink, true);
        return button;
    }

    private Button NavigationButton(Action click, bool forward)
    {
        var button = Button(OverviewParent, null, 0, 0, 0, 0, click);
        var rect = Rect("Chevron", button.transform, .5f, .5f, 0, 0);
        rect.pivot = new Vector2(.5f, .5f);
        rect.sizeDelta = new Vector2(20, 32);
        rect.localRotation = Quaternion.Euler(0, 0, forward ? 0 : 180);
        var icon = rect.gameObject.AddComponent<LiveBuildChevronGraphic>();
        icon.color = Ink;
        icon.raycastTarget = false;
        return button;
    }

    private TextMeshProUGUI Text(
        Transform parent,
        string value,
        float x,
        float y,
        float width,
        float height,
        int size,
        Color? color = null,
        bool center = false
    )
    {
        var text = Rect("Label", parent, x, y, width, height)
            .gameObject.AddComponent<TextMeshProUGUI>();
        _font!.Apply(text);
        text.text = value;
        text.richText = false;
        text.fontSize = size;
        text.fontSizeMin = size * .8f;
        text.fontSizeMax = size;
        text.enableAutoSizing = true;
#if BPP_ANDROID
        // The view factory owns authored sizes. TMP's current fontSize may already be autofitted.
        if (_overviewRoot == null || (parent != _overviewRoot && !parent.IsChildOf(_overviewRoot)))
        {
            _scaledLabels.Add((text, size));
            ApplyTextScale(text, size);
        }
#endif
        text.color = color ?? Ink;
        text.alignment = center ? TextAlignmentOptions.Center : TextAlignmentOptions.MidlineLeft;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        return text;
    }

#if BPP_ANDROID
    private void ApplyTextScale(TextMeshProUGUI text, int size)
    {
        text.fontSize = text.fontSizeMax = size * _textScale;
        text.fontSizeMin = size * .8f;
    }
#endif

    private bool ApplySafeAreaInsets()
    {
        if (_layout == null || _root == null)
            return false;
        var scale = CanvasScale;
        if (scale <= 0.001f)
            return false;
#if BPP_ANDROID
        var safe = AndroidOverlayViewport.ContentPixels;
#else
        var safe = Screen.safeArea;
#endif
        var insetLeft = Mathf.Max(0f, safe.xMin / scale);
        var insetRight = Mathf.Max(0f, (Screen.width - safe.xMax) / scale);
        var insetTop = Mathf.Max(0f, (Screen.height - safe.yMax) / scale);
        var insetBottom = Mathf.Max(0f, safe.yMin / scale);
        var min = new Vector2(insetLeft, insetBottom);
        var max = new Vector2(-insetRight, -insetTop);
        if (_layout.offsetMin == min && _layout.offsetMax == max)
            return false;
        _layout.offsetMin = min;
        _layout.offsetMax = max;
        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        _visible = false;
        _portraitLoad = null;
        _backdropLoad = null;
        _snapshot = null;
        _rows.Clear();
        _art.Clear();
        if (_root != null)
            UnityEngine.Object.Destroy(_root);
        _root = null;
    }
}
