#nullable enable
using BazaarPlusPlus.GameInterop;
using BazaarPlusPlus.Infrastructure;
using UnityEngine;
using UnityEngine.UIElements;

namespace BazaarPlusPlus.Game.CollectionPanel.Ui;

// Adds touch-style primary-button dragging to a ScrollView without stealing ordinary clicks
// from the filter controls it contains. The scrollbars and mouse-wheel behavior remain native.
internal sealed class ScrollViewDragScroller : IDisposable
{
    private const float DragThresholdPoints = 4f;

    private readonly ScrollView _scrollView;
    private readonly VisualElement _content;
    private int _pointerId = PointerId.invalidPointerId;
    private Vector2 _pointerDownPosition;
    private Vector2 _scrollOffsetAtPointerDown;
    private bool _isDragging;

    public ScrollViewDragScroller(ScrollView scrollView)
    {
        _scrollView = scrollView;
        _content = scrollView.contentContainer;
        _content.RegisterCallback<PointerDownEvent>(BppDelegates.EventCallback<PointerDownEvent>(OnPointerDown), TrickleDown.TrickleDown);
        _content.RegisterCallback<PointerMoveEvent>(BppDelegates.EventCallback<PointerMoveEvent>(OnPointerMove), TrickleDown.TrickleDown);
        _content.RegisterCallback<PointerUpEvent>(BppDelegates.EventCallback<PointerUpEvent>(OnPointerUp), TrickleDown.TrickleDown);
        _content.RegisterCallback<PointerCaptureOutEvent>(BppDelegates.EventCallback<PointerCaptureOutEvent>(OnPointerCaptureOut));
    }

    public void Dispose()
    {
        CancelDrag();
        _content.UnregisterCallback<PointerDownEvent>(BppDelegates.EventCallback<PointerDownEvent>(OnPointerDown), TrickleDown.TrickleDown);
        _content.UnregisterCallback<PointerMoveEvent>(BppDelegates.EventCallback<PointerMoveEvent>(OnPointerMove), TrickleDown.TrickleDown);
        _content.UnregisterCallback<PointerUpEvent>(BppDelegates.EventCallback<PointerUpEvent>(OnPointerUp), TrickleDown.TrickleDown);
        _content.UnregisterCallback<PointerCaptureOutEvent>(BppDelegates.EventCallback<PointerCaptureOutEvent>(OnPointerCaptureOut));
    }

    public void CancelDrag()
    {
        if (_pointerId != PointerId.invalidPointerId && BppPointerCapture.HasPointerCapture(_content, _pointerId))
            BppPointerCapture.ReleasePointer(_content, _pointerId);
        ResetDrag();
    }

    private void OnPointerDown(PointerDownEvent evt)
    {
        if (evt.button != 0 || _pointerId != PointerId.invalidPointerId)
            return;

        _pointerId = evt.pointerId;
        _pointerDownPosition = new Vector2(evt.position.x, evt.position.y);
        _scrollOffsetAtPointerDown = _scrollView.scrollOffset;
        _isDragging = false;
    }

    private void OnPointerMove(PointerMoveEvent evt)
    {
        if (evt.pointerId != _pointerId)
            return;

        var pointerPosition = new Vector2(evt.position.x, evt.position.y);
        var dragDelta = pointerPosition - _pointerDownPosition;
        if (!_isDragging && dragDelta.sqrMagnitude < DragThresholdPoints * DragThresholdPoints)
            return;

        if (!_isDragging)
        {
            _isDragging = true;
            BppPointerCapture.CapturePointer(_content, _pointerId);
        }

        _scrollView.scrollOffset = new Vector2(
            _scrollOffsetAtPointerDown.x,
            _scrollOffsetAtPointerDown.y - dragDelta.y
        );
        evt.StopPropagation();
    }

    private void OnPointerUp(PointerUpEvent evt)
    {
        if (evt.pointerId != _pointerId)
            return;

        var wasDragging = _isDragging;
        if (BppPointerCapture.HasPointerCapture(_content, _pointerId))
            BppPointerCapture.ReleasePointer(_content, _pointerId);
        ResetDrag();

        if (wasDragging)
            evt.StopPropagation();
    }

    private void OnPointerCaptureOut(PointerCaptureOutEvent evt)
    {
        if (evt.pointerId == _pointerId)
            ResetDrag();
    }

    private void ResetDrag()
    {
        _pointerId = PointerId.invalidPointerId;
        _isDragging = false;
    }
}
