#nullable enable
namespace BazaarPlusPlus.GameInterop.LiveCards;

/// <summary>
/// Best-effort identity of the item in a slot whose read failed. Every member is optional: the
/// property that identifies the item is often the same one that threw.
/// </summary>
internal readonly struct LiveCardSlotProbe
{
    internal LiveCardSlotProbe(Guid? templateId, int? itemSize)
    {
        TemplateId = templateId;
        ItemSize = itemSize;
    }

    internal Guid? TemplateId { get; }

    internal int? ItemSize { get; }
}

/// <summary>One slot whose read threw, reported so the caller can log it without aborting.</summary>
internal readonly struct LiveCardSlotFailure
{
    internal LiveCardSlotFailure(int slotIndex, LiveCardSlotProbe probe, Exception exception)
    {
        SlotIndex = slotIndex;
        Probe = probe;
        Exception = exception;
    }

    internal int SlotIndex { get; }

    internal LiveCardSlotProbe Probe { get; }

    internal Exception Exception { get; }
}

internal sealed class LiveCardSlotReadResult<T>
    where T : class
{
    internal LiveCardSlotReadResult(List<T> values, List<LiveCardSlotFailure> failures)
    {
        Values = values;
        Failures = failures;
    }

    /// <summary>The slots that read successfully, in slot order.</summary>
    internal IReadOnlyList<T> Values { get; }

    /// <summary>The slots that threw, in slot order.</summary>
    internal IReadOnlyList<LiveCardSlotFailure> Failures { get; }
}

/// <summary>
/// Reads one container socket or shop entry at a time and isolates a throwing read, so one
/// malformed native item cannot abort the whole section (or make an entire run look empty).
/// Unity-free by design: the caller owns the native access, this owns ordering and failure
/// bookkeeping only, which is what the pure tests exercise.
/// </summary>
internal static class LiveCardSlotReader
{
    /// <summary>
    /// Invokes <paramref name="read"/> for every slot. A returned <c>null</c> means the slot was
    /// deliberately skipped (empty socket, duplicate, wrong card type) and is reported neither as a
    /// value nor as a failure. A throw is recorded with <paramref name="probe"/>'s best-effort
    /// identity, and the next slot still runs.
    /// </summary>
    internal static LiveCardSlotReadResult<T> Read<T>(
        int slotCount,
        Func<int, T?> read,
        Func<int, LiveCardSlotProbe>? probe = null
    )
        where T : class
    {
        var values = new List<T>(slotCount);
        var failures = new List<LiveCardSlotFailure>();
        for (var slot = 0; slot < slotCount; slot++)
        {
            try
            {
                if (read(slot) is { } value)
                    values.Add(value);
            }
            catch (Exception exception)
            {
                failures.Add(new LiveCardSlotFailure(slot, Probe(probe, slot), exception));
            }
        }

        return new LiveCardSlotReadResult<T>(values, failures);
    }

    private static LiveCardSlotProbe Probe(Func<int, LiveCardSlotProbe>? probe, int slot)
    {
        if (probe == null)
            return default;

        try
        {
            return probe(slot);
        }
        catch
        {
            // Identifying the failed item must never turn one logged failure into two.
            return default;
        }
    }
}
