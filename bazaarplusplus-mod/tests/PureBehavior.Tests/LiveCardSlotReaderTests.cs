using BazaarPlusPlus.GameInterop.LiveCards;
using Xunit;

public sealed class LiveCardSlotReaderTests
{
    [Fact]
    public void A_throwing_slot_is_reported_without_stopping_later_slots()
    {
        var visited = new List<int>();
        var read = LiveCardSlotReader.Read<string>(
            4,
            slot =>
            {
                visited.Add(slot);
                return slot == 1 ? throw new InvalidOperationException("bad slot") : $"card-{slot}";
            }
        );

        Assert.Equal(new[] { 0, 1, 2, 3 }, visited);
        Assert.Equal(new[] { "card-0", "card-2", "card-3" }, read.Values);
        var failure = Assert.Single(read.Failures);
        Assert.Equal(1, failure.SlotIndex);
        Assert.IsType<InvalidOperationException>(failure.Exception);
    }

    [Fact]
    public void Skipped_slots_report_neither_values_nor_failures()
    {
        var read = LiveCardSlotReader.Read<string>(3, slot => slot == 1 ? null : $"card-{slot}");

        Assert.Equal(new[] { "card-0", "card-2" }, read.Values);
        Assert.Empty(read.Failures);
    }

    [Fact]
    public void Failure_probe_carries_best_effort_identity_and_never_escapes_the_reader()
    {
        var templateId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var read = LiveCardSlotReader.Read<string>(
            2,
            _ => throw new InvalidOperationException("bad slot"),
            slot =>
                slot == 0
                    ? new LiveCardSlotProbe(templateId, 2)
                    : throw new ArgumentException("probe failed too")
        );

        Assert.Equal(2, read.Failures.Count);
        Assert.Equal(templateId, read.Failures[0].Probe.TemplateId);
        Assert.Equal(2, read.Failures[0].Probe.ItemSize);
        Assert.Null(read.Failures[1].Probe.TemplateId);
        Assert.Null(read.Failures[1].Probe.ItemSize);
    }

    [Fact]
    public void Every_failure_is_kept_in_slot_order()
    {
        var read = LiveCardSlotReader.Read<string>(
            3,
            slot => slot == 1 ? "ok" : throw new InvalidOperationException($"bad {slot}")
        );

        Assert.Equal(new[] { "ok" }, read.Values);
        Assert.Equal(new[] { 0, 2 }, read.Failures.Select(failure => failure.SlotIndex));
    }

    [Fact]
    public void An_empty_range_returns_no_values_and_no_failures()
    {
        var read = LiveCardSlotReader.Read<string>(0, _ => "card");

        Assert.Empty(read.Values);
        Assert.Empty(read.Failures);
    }
}
