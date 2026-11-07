namespace BazaarHelper;

// No wall clock, game speed override or pause control. A frame callback is the only advance source.
internal sealed class CombatFrameClock
{
    private int processed, total;
    internal bool Active
    {
        get; private set;
    }
    internal bool HasCombat
    {
        get; private set;
    }
    internal int ElapsedMilliseconds => (int)Math.Min(int.MaxValue, Math.Max((long)processed - 1, 0) * 50);
    internal void Begin(int frames)
    {
        total = Math.Max(frames, 0);
        processed = 0;
        Active = HasCombat = true;
    }
    internal void Advance()
    {
        if (Active && processed < total)
            processed++;
    }
    internal void End()
    {
        Active = false;
    }
    internal void Reset()
    {
        processed = total = 0;
        Active = HasCombat = false;
    }
}
