#nullable enable
#pragma warning disable CS0436
using System.Reflection;
using BazaarGameShared.Infra.Messages;
using HarmonyLib;
using TheBazaar;

namespace BazaarPlusPlus.Patches;

/// <summary>
/// Resolves, per game-build shape, the single NetMessageProcessor method that every
/// live-run net message flows through.
/// </summary>
internal static class NetMessageDispatchSeam
{
    // PTR moved dispatch into a private Receive(INetMessage, bool) and unwraps
    // NetMessageAggregate children by recursing through it, so patching the public
    // one-arg ReceiveOrQueue there would silently miss aggregated messages. Online has
    // no such method; its public ReceiveOrQueue self-recurses aggregate children.
    // Preferring the private method when present therefore targets, on each build, the
    // one method every message (including aggregate children) passes through.
    internal static MethodBase? ResolveTarget()
    {
        return AccessTools.Method(
                typeof(NetMessageProcessor),
                "Receive",
                new[] { typeof(INetMessage), typeof(bool) }
            )
            ?? AccessTools.Method(
                typeof(NetMessageProcessor),
                "ReceiveOrQueue",
                new[] { typeof(INetMessage) }
            );
    }

    // On the PTR target the second argument is true only for tournament spectate
    // playback, which must not be recorded as the player's own run.
    internal static bool IsSpectatePlayback(object[] args)
    {
        return args.Length > 1 && args[1] is true;
    }

    internal static IEnumerable<INetMessage> ExpandForObservation(INetMessage message)
    {
#if BPP_ANDROID
        // Android's metadata/data hook sees the outer call; the AOT self-call at
        // ReceiveOrQueue's aggregate loop goes directly to the original code.
        var pending = new Stack<INetMessage>();
        pending.Push(message);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current.TryCast<NetMessageAggregate>() is { } aggregate)
            {
                var children = aggregate.Messages;
                if (children != null)
                    for (var index = children.Count - 1; index >= 0; index--)
                        if (children[index] is { } child) pending.Push(child);
            }
            else yield return current;
        }
#else
        yield return message;
#endif
    }
}
