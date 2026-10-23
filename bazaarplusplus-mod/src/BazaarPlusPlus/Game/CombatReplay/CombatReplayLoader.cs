#nullable enable
using BazaarPlusPlus.GameInterop;
using BazaarGameShared;
using BazaarGameShared.Infra.Messages;
using BazaarPlusPlus.Game.PvpBattles;
using TheBazaar;

namespace BazaarPlusPlus.Game.CombatReplay;

internal sealed class CombatReplayLoader
{
    public CombatSequenceMessages Load(PvpReplayPayload payload)
    {
        if (payload == null)
            throw new ArgumentNullException(nameof(payload));

        return new CombatSequenceMessages(
            DeserializeGameSim(payload.SpawnMessageBytes),
            DeserializeGameSim(payload.DespawnMessageBytes),
            DeserializeCombatSim(payload.CombatMessageBytes)
        );
    }

    private static NetMessageGameSim DeserializeGameSim(byte[] payload)
    {
        return BppMessagePack.Deserialize<NetMessageGameSim>(payload);
    }

    private static NetMessageCombatSim DeserializeCombatSim(byte[] payload)
    {
        return BppMessagePack.Deserialize<NetMessageCombatSim>(payload);
    }
}
