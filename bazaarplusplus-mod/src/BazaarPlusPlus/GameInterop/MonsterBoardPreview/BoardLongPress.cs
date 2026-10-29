#nullable enable
namespace BazaarPlusPlus.GameInterop.MonsterBoardPreview;

// Touch has no hover, so a long press is the board previews' explicit inspection gesture.
// A press fires once after it has been held still long enough. The release that ends a fired
// press belongs to that inspection and must not also read as a tap.
internal sealed class BoardLongPress
{
    internal const float HoldSeconds = .45f;

    private readonly float _slopPixels;
    private bool _down;
    private float _startTime;

    internal BoardLongPress(float slopPixels) => _slopPixels = slopPixels;

    /// <summary>True from the frame the current or most recent press fired until the next press.</summary>
    internal bool Fired { get; private set; }

    internal float StartX { get; private set; }
    internal float StartY { get; private set; }

    /// <summary>Returns true only on the frame the hold threshold is crossed.</summary>
    internal bool Sample(bool pressedThisFrame, bool isPressed, float x, float y, float time)
    {
        if (pressedThisFrame)
        {
            _down = true;
            Fired = false;
            _startTime = time;
            StartX = x;
            StartY = y;
        }
        if (!_down)
            return false;
        var dx = x - StartX;
        var dy = y - StartY;
        if (!isPressed || dx * dx + dy * dy > _slopPixels * _slopPixels)
        {
            _down = false;
            return false;
        }
        if (Fired || time - _startTime < HoldSeconds)
            return false;
        Fired = true;
        return true;
    }

    internal void Cancel()
    {
        _down = false;
        Fired = false;
    }
}
