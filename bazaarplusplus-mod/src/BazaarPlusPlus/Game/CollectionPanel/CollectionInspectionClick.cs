#nullable enable
namespace BazaarPlusPlus.Game.CollectionPanel;

internal sealed class CollectionInspectionClick
{
    // Physical pixels. A finger routinely moves several pixels between down and up; the old
    // 6px tolerance cancelled most real taps on a 640dpi phone. 32px is still well below a
    // deliberate drag and keeps scrolling cancelled by the separate scroll check.
    internal const float TapSlopPixels = 32f;
    private Guid? _card;
    private float _x,
        _y,
        _scroll;

    public bool Sample(bool pressed, bool released, Guid? card, float x, float y, float scroll)
    {
        if (pressed)
        {
            _card = card;
            _x = x;
            _y = y;
            _scroll = scroll;
        }
        if (
            _card != card
            || (x - _x) * (x - _x) + (y - _y) * (y - _y)
                > TapSlopPixels * TapSlopPixels
            || Math.Abs(scroll - _scroll) > .5f
        )
            Reset();
        if (!released)
            return false;
        var open = _card.HasValue;
        Reset();
        return open;
    }

    public void Reset() => _card = null;
}
