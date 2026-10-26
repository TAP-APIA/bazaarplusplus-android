#nullable enable

using BazaarGameClient.Domain.Cards;
using BazaarGameClient.Domain.Models;
using BazaarGameClient.Domain.Models.Cards;
using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarPlusPlus.GameInterop.Cards;
using TheBazaar;

namespace BazaarPlusPlus.GameInterop.LiveCards;

internal sealed class LiveCardSnapshotReader
{
    public LiveCardSnapshotReadOutcome Read()
    {
        var issues = new List<LiveCardSnapshotIssue>();
        try
        {
            var run = Data.Run;
            if (run?.Player == null)
                return new LiveCardSnapshotReadOutcome(LiveCardSnapshotSet.Empty, issues);

            var runState = Data.CurrentState.AsIl2Cpp<RunState>();
            return new LiveCardSnapshotReadOutcome(
                new LiveCardSnapshotSet(
                    run.Player.Hero,
                    ReadShopItems(runState, issues),
                    ReadContainerItems(run.Player.Hand, LiveCardSnapshotSection.Board, issues),
                    ReadContainerItems(run.Player.Stash, LiveCardSnapshotSection.Stash, issues)
                ),
                issues
            );
        }
        catch (Exception ex)
        {
            // Last resort: one section's native root being unreadable must not hide the sections
            // that did read, but this path only runs when the isolation below cannot apply.
            issues.Add(
                new LiveCardSnapshotIssue(
                    LiveCardSnapshotSection.All,
                    LiveCardSnapshotFailureReason.ReadException,
                    templateId: null,
                    socketId: null,
                    itemSize: null,
                    exception: ex
                )
            );
            return new LiveCardSnapshotReadOutcome(LiveCardSnapshotSet.Empty, issues);
        }
    }

    private static IReadOnlyList<LiveCardSnapshot> ReadShopItems(
        RunState? runState,
        List<LiveCardSnapshotIssue> issues
    )
    {
        var result = new List<LiveCardSnapshot>();
        if (runState?.SelectionSet is not { } selection)
            return result;

        var order = 0;
        ItemCard? current = null;
        var read = LiveCardSlotReader.Read<LiveCardSnapshot>(
            selection.Count,
            slot =>
            {
                current = null;
                var entry = selection[slot];
                if (string.IsNullOrEmpty(entry))
                    return null;

                var itemCard = ResolveShopItem(entry);
                if (itemCard == null)
                    return null;

                current = itemCard;
                var snapshot = BuildSnapshot(itemCard, order, socketId: null);
                order++;
                return snapshot;
            },
            _ => ProbeItem(current)
        );

        foreach (var failure in read.Failures)
            AddReadException(issues, LiveCardSnapshotSection.Shop, failure, socketId: null);

        result.AddRange(read.Values);
        return result;
    }

    private static ItemCard? ResolveShopItem(string entry)
    {
#if BPP_ANDROID
        return Data.GetCard(entry).AsIl2Cpp<ItemCard>();
#else
        var instanceId = InstanceId.TryParse(entry);
        return Data.Entities.TryGetValue(instanceId, out var card)
            ? card.AsIl2Cpp<ItemCard>()
            : null;
#endif
    }

    private static IReadOnlyList<LiveCardSnapshot> ReadContainerItems(
        object? inventory,
        LiveCardSnapshotSection section,
        List<LiveCardSnapshotIssue> issues
    )
    {
        if (inventory == null)
            return Array.Empty<LiveCardSnapshot>();

        var result = new List<LiveCardSnapshot>();
        try
        {
            var container = inventory.AsIl2Cpp<CardContainer>()?.Container;
            if (container == null)
                return Array.Empty<LiveCardSnapshot>();

            ReadContainerSlots(container, section, result, issues);
        }
        catch (Exception ex)
        {
            // The container root itself is unreadable; keep whatever earlier slots produced and
            // let the other sections take their own read.
            AddSectionReadException(issues, section, ex);
        }
        return result;
    }

    private static void ReadContainerSlots(
        SocketedContainer container,
        LiveCardSnapshotSection section,
        List<LiveCardSnapshot> result,
        List<LiveCardSnapshotIssue> issues
    )
    {
        var order = 0;
        ItemCard? current = null;
#if BPP_ANDROID
        var sockets = container.Sockets;
        if (sockets == null)
            return;

        var seen = new HashSet<IntPtr>();
        var read = LiveCardSlotReader.Read<LiveCardSnapshot>(
            sockets.Length,
            slot =>
            {
                current = null;
                var socketable = sockets[slot];
                if (socketable == null || !seen.Add(socketable.Pointer))
                    return null;

                var socketId = (EContainerSocketId)slot;
                var itemCard = socketable.TryCast<ItemCard>();
                if (itemCard == null)
                    return null;

                current = itemCard;
                if (!CanFitSocket(itemCard.Size, socketId))
                {
                    AddInvalidPlacement(issues, section, itemCard, socketId);
                    return null;
                }

                var snapshot = BuildSnapshot(itemCard, order, socketId);
                order++;
                return snapshot;
            },
            slot => ProbeSocket(sockets[slot])
        );
#else
        var pairs = container.GetCardsAndSockets().ToManaged();
        var read = LiveCardSlotReader.Read<LiveCardSnapshot>(
            pairs.Count,
            slot =>
            {
                current = null;
                var socketId = pairs[slot].Item2;
                var itemCard = pairs[slot].Item1.AsIl2Cpp<ItemCard>();
                if (itemCard == null)
                    return null;

                current = itemCard;
                if (!CanFitSocket(itemCard.Size, socketId))
                {
                    AddInvalidPlacement(issues, section, itemCard, socketId);
                    return null;
                }

                var snapshot = BuildSnapshot(itemCard, order, socketId);
                order++;
                return snapshot;
            },
            slot => ProbeSocket(pairs[slot].Item1)
        );
#endif

        foreach (var failure in read.Failures)
            AddReadException(issues, section, failure, (EContainerSocketId)failure.SlotIndex);

        result.AddRange(read.Values);
    }

    private static LiveCardSnapshot BuildSnapshot(
        ItemCard card,
        int order,
        EContainerSocketId? socketId
    )
    {
        string instanceId;
        try
        {
            instanceId = card.InstanceId.Value ?? string.Empty;
        }
        catch
        {
            instanceId = string.Empty;
        }

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

        return new LiveCardSnapshot
        {
            InstanceId = instanceId,
            TemplateId = card.TemplateId.ToSystemGuid(),
            Order = order,
            Tier = card.Tier,
            Size = card.Size,
            EnchantmentType = NativeCardOptionalFields.Enchantment(card),
            SocketId = socketId,
            Attributes = attributes,
        };
    }

    private static bool CanFitSocket(ECardSize size, EContainerSocketId socketId)
    {
        var span = CardSizeSpan.Resolve(size);
        return (int)socketId >= 0 && (int)socketId + span <= SocketedContainer.SocketCount;
    }

    private static void AddInvalidPlacement(
        List<LiveCardSnapshotIssue> issues,
        LiveCardSnapshotSection section,
        ItemCard itemCard,
        EContainerSocketId socketId
    )
    {
        var probe = ProbeItem(itemCard);
        issues.Add(
            new LiveCardSnapshotIssue(
                section,
                LiveCardSnapshotFailureReason.InvalidPlacement,
                probe.TemplateId,
                socketId,
                probe.ItemSize is { } size ? (ECardSize)size : null,
                exception: null
            )
        );
    }

    private static void AddReadException(
        List<LiveCardSnapshotIssue> issues,
        LiveCardSnapshotSection section,
        LiveCardSlotFailure failure,
        EContainerSocketId? socketId
    ) =>
        issues.Add(
            new LiveCardSnapshotIssue(
                section,
                LiveCardSnapshotFailureReason.ReadException,
                failure.Probe.TemplateId,
                socketId,
                failure.Probe.ItemSize is { } size ? (ECardSize)size : null,
                failure.Exception
            )
        );

    private static void AddSectionReadException(
        List<LiveCardSnapshotIssue> issues,
        LiveCardSnapshotSection section,
        Exception exception
    ) =>
        issues.Add(
            new LiveCardSnapshotIssue(
                section,
                LiveCardSnapshotFailureReason.ReadException,
                templateId: null,
                socketId: null,
                itemSize: null,
                exception: exception
            )
        );

    private static LiveCardSlotProbe ProbeSocket(ISocketable? socketable) =>
        ProbeItem(socketable.AsIl2Cpp<ItemCard>());

    private static LiveCardSlotProbe ProbeItem(ItemCard? card)
    {
        if (card is not { } item)
            return default;

        return new LiveCardSlotProbe(
            TryRead(() => item.TemplateId.ToSystemGuid()),
            TryRead(() => (int)item.Size)
        );
    }

    private static T? TryRead<T>(Func<T> read)
        where T : struct
    {
        try
        {
            return read();
        }
        catch
        {
            // The probe only decorates a failure that is already reported; a second throw must not
            // escape the isolation loop.
            return null;
        }
    }
}

internal enum LiveCardSnapshotSection
{
    All,
    Board,
    Stash,
    Shop,
}

internal enum LiveCardSnapshotFailureReason
{
    ReadException,
    InvalidPlacement,
}

internal sealed class LiveCardSnapshotIssue
{
    internal LiveCardSnapshotIssue(
        LiveCardSnapshotSection section,
        LiveCardSnapshotFailureReason reason,
        Guid? templateId,
        EContainerSocketId? socketId,
        ECardSize? itemSize,
        Exception? exception
    )
    {
        Section = section;
        Reason = reason;
        TemplateId = templateId;
        SocketId = socketId;
        ItemSize = itemSize;
        Exception = exception;
    }

    internal LiveCardSnapshotSection Section { get; }
    internal LiveCardSnapshotFailureReason Reason { get; }
    internal Guid? TemplateId { get; }
    internal EContainerSocketId? SocketId { get; }
    internal ECardSize? ItemSize { get; }
    internal Exception? Exception { get; }
}

internal sealed class LiveCardSnapshotReadOutcome
{
    internal LiveCardSnapshotReadOutcome(
        LiveCardSnapshotSet snapshot,
        IReadOnlyList<LiveCardSnapshotIssue> issues
    )
    {
        Snapshot = snapshot;
        Issues = issues;
    }

    internal LiveCardSnapshotSet Snapshot { get; }
    internal IReadOnlyList<LiveCardSnapshotIssue> Issues { get; }
}
