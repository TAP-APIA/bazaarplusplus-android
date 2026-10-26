#nullable enable
using BazaarPlusPlus.Game.LiveBuildPanel.Data;
using BazaarPlusPlus.Game.LiveBuildPanel.Ui;
using BazaarPlusPlus.GameInterop.CardPreview;
using BazaarPlusPlus.GameInterop.ItemBoardPreview;
using BazaarPlusPlus.GameInterop.MonsterBoardPreview;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using BazaarPlusPlus.Infrastructure.UiTokens;
using UnityEngine;

namespace BazaarPlusPlus.Game.LiveBuildPanel.Preview;

internal sealed class LiveBuildPreviewRenderer : IDisposable
{
    private readonly Transform _parent;
    private readonly LiveBuildPanelView _view;
    private readonly Action<Guid> _toggle;
    private readonly Dictionary<BppItemBoardId, OwnedMonsterBoardPreview> _rows = new();
    private readonly Dictionary<BppItemBoardId, Rect> _bounds = new();
    private LiveBuildPanelSnapshot? _snapshot;
    private LiveItemBoardRowVm[] _rowModels = Array.Empty<LiveItemBoardRowVm>();
    private HashSet<Guid> _candidates = new();
    private readonly Dictionary<BppItemBoardId, int> _markerVersions = new();
    private readonly Dictionary<BppItemBoardId, bool> _visible = new();

    // The four boards are peers with one region and one carpet prefab, so one carpet rail
    // positions every header. Dirty means "ask again": a preview that has not finished loading
    // keeps it set for the next tick.
    private Rect? _sharedRail;
    private bool _railsDirty = true;
    private float _footerPixels;
    private int _presentationRevision = -1;
#if BPP_ANDROID
    // Candidate taps commit on release so a scroll that starts on a carpet does not
    // toggle the card under the finger.
    private bool _tapPending;
    private Guid _tapTemplate;
    private Vector2 _tapPointer;
#endif

    // Parking keeps the preview's size (and therefore its fit) while moving the whole
    // overlay canvas off-screen; a native preview canvas cannot be clipped by the uGUI mask.
    private const float ParkedScreenY = -100000f;

    internal LiveBuildPreviewRenderer(
        Transform parent,
        LiveBuildPanelView view,
        Action<Guid> toggle
    )
    {
        _parent = parent;
        _view = view;
        _toggle = toggle;
    }

    internal void SetBounds(BppItemBoardId id, Rect bounds)
    {
        _bounds[id] = bounds;
        if (_rows.TryGetValue(id, out var row))
            ApplyBounds(id, row);
    }

    // The row's real bounds stay cached; only the preview presented to the screen is parked
    // when the row scrolls out of the board viewport. Size is preserved, so the fit scale
    // stays the same even though the preview is moved.
    private void ApplyBounds(BppItemBoardId id, OwnedMonsterBoardPreview preview)
    {
        if (!_bounds.TryGetValue(id, out var bounds))
            return;
        preview.SetBounds(
            _view.IsRowVisible(id)
                ? bounds
                : new Rect(bounds.x, ParkedScreenY, bounds.width, bounds.height),
            _view.BoardFooterPixels,
            _view.BoardTopPixels
        );
    }

    internal void Render(LiveBuildPanelSnapshot snapshot)
    {
        _markerVersions.Clear();
        _railsDirty = true;
        _snapshot = snapshot;
        _rowModels = snapshot.Rows;
        _candidates = new HashSet<Guid>(snapshot.CandidateTemplateIds);
        if (!_view.IsCreated)
            return;
        foreach (var row in _rowModels)
        {
            var board = row.Board;
            if (!_rows.TryGetValue(board.Id, out var preview))
            {
                preview = new OwnedMonsterBoardPreview(
                    _parent,
                    BppOverlaySorting.NativeCardPreview,
                    false,
                    (status, exception) =>
                    {
                        _view.SetBoardStatus(
                            board.Id,
                            status,
                            (exception as NativeBoardPartialFailure)?.Count ?? 0
                        );
                        if (exception != null)
                            BppLog.WarnEvent(
                                new BppLogEvent(
                                    BppLogFeatureScope.LiveBuildPanel,
                                    "live_build_panel.card_preview.degraded",
                                    storm: ["operation", "reason_code"]
                                ),
                                exception,
                                ("operation", NativeCardPreviewOperation.SetUp),
                                ("reason_code", NativeCardPreviewFailureReason.SetUpException),
                                ("template_id", null)
                            );
                    },
                    itemsOnly: true
                );
                _rows.Add(board.Id, preview);
            }
            if (_bounds.ContainsKey(board.Id))
                ApplyBounds(board.Id, preview);
            preview.Render(
                board.Signature,
                NativeMonsterBoardItemMapper.Map(board, $"live-build-{board.Id}"),
                new()
            );
        }
        UpdateMarkers();
    }

    internal void Tick(Vector2? pointer, bool clicked)
    {
        if (_snapshot == null)
            return;
        if (!_view.IsCreated)
            return;
        if (_presentationRevision != _view.PresentationRevision)
        {
            _presentationRevision = _view.PresentationRevision;
            _markerVersions.Clear();
            _railsDirty = true;
        }
        if (!Mathf.Approximately(_footerPixels, _view.BoardFooterPixels))
        {
            _footerPixels = _view.BoardFooterPixels;
            _markerVersions.Clear();
            foreach (var pair in _rows)
                ApplyBounds(pair.Key, pair.Value);
        }
        if (_rows.Count == 0)
            Render(_snapshot);
        SyncVisibility();
        // Finish iterating before toggling: the callback may replace the recommendation.
        Guid? selected = null;
        var pointerPoint = pointer ?? new Vector2(-1, -1);
        var pointerOnBoard = _view.BoardContainsPointer(pointerPoint);
        foreach (var row in _rowModels)
        {
            if (!_rows.TryGetValue(row.Board.Id, out var preview))
                continue;
            if (!_view.IsRowVisible(row.Board.Id))
                continue;
            preview.Fit();
            var template = preview.TrackPointer(
                pointerOnBoard ? pointerPoint : new Vector2(-1, -1)
            );
            if (clicked && pointerOnBoard && row.CanToggleCandidates && template.HasValue)
            {
#if BPP_ANDROID
                // A scroll that starts on a carpet must not toggle its card. Hold the tap
                // until the finger lifts without crossing the drag threshold.
                _tapPending = true;
                _tapTemplate = template.Value;
                _tapPointer = pointer ?? new Vector2(-1, -1);
#else
                selected = template;
#endif
            }
        }
#if BPP_ANDROID
        if (_tapPending)
        {
            if (
                pointer is { } current
                && Vector2.Distance(current, _tapPointer)
                    > LiveBuildPanelLayout.BoardDragThresholdPixels
            )
                _tapPending = false;
            else if (_view.PointerReleasedThisFrame)
            {
                selected = _tapTemplate;
                _tapPending = false;
            }
            else if (pointer == null)
            {
                selected = _tapTemplate;
                _tapPending = false;
            }
        }
#endif
        if (selected.HasValue)
            _toggle(selected.Value);
        UpdateMarkers();
    }

    // The view republishes bounds while scrolling, but visibility can also flip without a
    // size change. Park/restore here so marker state and pointer tracking follow it.
    private void SyncVisibility()
    {
        foreach (var pair in _rows)
        {
            var visible = _view.IsRowVisible(pair.Key);
            if (_visible.TryGetValue(pair.Key, out var previous) && previous == visible)
                continue;
            _visible[pair.Key] = visible;
            ApplyBounds(pair.Key, pair.Value);
            _markerVersions.Remove(pair.Key);
            _railsDirty = true;
        }
    }

    private void UpdateMarkers()
    {
        if (_snapshot == null)
            return;
        SyncRails();
        foreach (var row in _rowModels)
        {
            // A parked row keeps no markers, and a peeking row keeps none either: tags live on
            // an unmasked overlay canvas and would float over the footer below a partial carpet.
            var complete = _view.IsRowComplete(row.Board.Id);
            var version =
                complete && _rows.TryGetValue(row.Board.Id, out var boardPreview)
                    ? boardPreview.GeometryVersion
                    : int.MinValue;
            if (_markerVersions.TryGetValue(row.Board.Id, out var previous) && previous == version)
                continue;
            _markerVersions[row.Board.Id] = version;
            _view.BeginMarkers(row.Board.Id);
            if (complete && _rows.TryGetValue(row.Board.Id, out var preview))
            {
                preview.VisitItems(item =>
                {
                    if (_candidates.Contains(item.TemplateId))
                        _view.AddMarker(row.Board.Id, item.Bounds, row.CanToggleCandidates);
                });
            }
            _view.EndMarkers(row.Board.Id);
        }
    }

    // Every row needs the rail, including a row whose header peeks in while its carpet is
    // still below the mask. The rails are peer-identical, so the first ready visible carpet
    // positions all four headers.
    private void SyncRails()
    {
        if (!_railsDirty)
            return;
        Rect? found = null;
        foreach (var pair in _rows)
        {
            if (!_view.IsRowVisible(pair.Key))
                continue;
            if (pair.Value.TryGetCarpetBounds(out var carpet))
            {
                found = carpet;
                break;
            }
        }
        if (found is not { } rail)
        {
            // Retry on the next tick: a rendered preview acquires its carpet asynchronously.
            return;
        }
        _railsDirty = false;
        if (
            _sharedRail is { } previous
            && Mathf.Abs(previous.x - rail.x) < .5f
            && Mathf.Abs(previous.width - rail.width) < .5f
        )
            return;
        _sharedRail = rail;
        foreach (var pair in _rows)
            _view.SetBoardRail(pair.Key, rail);
    }

    internal void Hide()
    {
        foreach (var preview in _rows.Values)
            preview.Dispose();
        _rows.Clear();
        _markerVersions.Clear();
        _visible.Clear();
        _sharedRail = null;
        _railsDirty = true;
        _snapshot = null;
        _rowModels = Array.Empty<LiveItemBoardRowVm>();
        _candidates.Clear();
#if BPP_ANDROID
        _tapPending = false;
#endif
    }

    public void Dispose() => Hide();
}
