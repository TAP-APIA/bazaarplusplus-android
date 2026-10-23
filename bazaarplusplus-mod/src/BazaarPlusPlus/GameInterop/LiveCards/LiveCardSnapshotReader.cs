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
                    ReadShopItems(runState),
                    ReadContainerItems(run.Player.Hand, LiveCardSnapshotSection.Board, issues),
                    ReadContainerItems(run.Player.Stash, LiveCardSnapshotSection.Stash, issues)
                ),
                issues
            );
        }
        catch (Exception ex)
        {
            issues.Add(
                new LiveCardSnapshotIssue(
                    LiveCardSnapshotSection.All,
                    LiveCardSnapshotFailureReason.ReadException,
                    templateId: null,
                    socketId: null,
                    itemSize: null,
                    ex
                )
            );
            return new LiveCardSnapshotReadOutcome(LiveCardSnapshotSet.Empty, issues);
        }
    }

    private static IReadOnlyList<LiveCardSnapshot> ReadShopItems(RunState? runState)
    {
        var result = new List<LiveCardSnapshot>();
        if (runState?.SelectionSet == null)
            return result;

        var order = 0;
#if BPP_ANDROID
        foreach (var entry in runState.SelectionSet)
        {
            if (string.IsNullOrEmpty(entry))
                continue;
            Card? card;
            try
            {
                card = Data.GetCard(entry);
            }
            catch
            {
                continue;
            }
            if (card.AsIl2Cpp<ItemCard>() is not { } itemCard)
                continue;

            result.Add(BuildSnapshot(itemCard, order++, socketId: null));
        }
#else
        foreach (var entry in runState.SelectionSet)
        {
            var instanceId = InstanceId.TryParse(entry);
            if (!Data.Entities.TryGetValue(instanceId, out var card))
                continue;
            if (card is not ItemCard itemCard)
                continue;

            result.Add(BuildSnapshot(itemCard, order++, socketId: null));
        }
#endif

        return result;
    }

    private static IReadOnlyList<LiveCardSnapshot> ReadContainerItems(
        object? inventory,
        LiveCardSnapshotSection section,
        List<LiveCardSnapshotIssue> issues
    )
    {
        if (inventory == null)
            return Array.Empty<LiveCardSnapshot>();

        var container = inventory.AsIl2Cpp<CardContainer>()?.Container;
        if (container == null)
            return Array.Empty<LiveCardSnapshot>();

        var result = new List<LiveCardSnapshot>();
        var order = 0;
#if BPP_ANDROID
        var sockets = container.Sockets;
        if (sockets == null)
            return result;
        var seen = new HashSet<IntPtr>();
        for (var i = 0; i < sockets.Length; i++)
        {
            var socketable = sockets[i];
            if (socketable == null || !seen.Add(socketable.Pointer))
                continue;
            var socketId = (EContainerSocketId)i;
            var itemCard = socketable.TryCast<ItemCard>();
            if (itemCard == null)
                continue;
            if (!CanFitSocket(itemCard.Size, socketId))
            {
                issues.Add(
                    new LiveCardSnapshotIssue(
                        section,
                        LiveCardSnapshotFailureReason.InvalidPlacement,
                        itemCard.TemplateId.ToSystemGuid(),
                        socketId,
                        itemCard.Size,
                        exception: null
                    )
                );
                continue;
            }

            result.Add(BuildSnapshot(itemCard, order++, socketId));
        }
#else
        foreach (var socketPair in container.GetCardsAndSockets().ToManaged())
        {
            var socketable = socketPair.Item1;
            var socketId = socketPair.Item2;
            var itemCard = socketable.TryCast<ItemCard>();
            if (itemCard == null)
                continue;
            if (!CanFitSocket(itemCard.Size, socketId))
            {
                issues.Add(
                    new LiveCardSnapshotIssue(
                        section,
                        LiveCardSnapshotFailureReason.InvalidPlacement,
                        itemCard.TemplateId.ToSystemGuid(),
                        socketId,
                        itemCard.Size,
                        exception: null
                    )
                );
                continue;
            }

            result.Add(BuildSnapshot(itemCard, order++, socketId));
        }
#endif

        return result;
    }

    private static LiveCardSnapshot BuildSnapshot(
        ItemCard card,
        int order,
        EContainerSocketId? socketId
    )
    {
        return new LiveCardSnapshot
        {
            InstanceId = card.InstanceId.Value ?? string.Empty,
            TemplateId = card.TemplateId.ToSystemGuid(),
            Order = order,
            Tier = card.Tier,
            Size = card.Size,
            EnchantmentType = card.Enchantment.ToManagedNullable(),
            SocketId = socketId,
            Attributes =
                card.Attributes != null
                    ? new Dictionary<ECardAttributeType, int>(card.Attributes.ToManagedDictionary())
                    : new Dictionary<ECardAttributeType, int>(),
        };
    }

    private static bool CanFitSocket(ECardSize size, EContainerSocketId socketId)
    {
        var span = CardSizeSpan.Resolve(size);
        return (int)socketId >= 0 && (int)socketId + span <= SocketedContainer.SocketCount;
    }
}

internal enum LiveCardSnapshotSection
{
    All,
    Board,
    Stash,
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
