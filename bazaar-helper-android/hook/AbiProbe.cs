using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Domain.Core.Types;
namespace BazaarHelper;
/** Constructs only in-memory DTOs; never emits a game message, record, or HTTP request. */
internal static class AbiProbe
{
    internal static void Verify()
    {
        var card = new SimUpdateCard();
        if (OptionalValue.Read<ECardSize>(card, "Size") != null || OptionalValue.Read<ETier>(card, "Tier") != null || OptionalValue.Read<EEnchantmentType>(card, "Enchantment") != null)
            throw new InvalidDataException("Empty native nullable probe failed");
        card.Size = new Il2CppSystem.Nullable<ECardSize>((ECardSize)0);
        card.Tier = new Il2CppSystem.Nullable<ETier>((ETier)0);
        card.Enchantment = new Il2CppSystem.Nullable<EEnchantmentType>((EEnchantmentType)0);
        if (OptionalValue.Read<ECardSize>(card, "Size") != (ECardSize)0 || OptionalValue.Read<ETier>(card, "Tier") != (ETier)0 || OptionalValue.Read<EEnchantmentType>(card, "Enchantment") != (EEnchantmentType)0)
            throw new InvalidDataException("Native nullable enum-zero probe failed");
    }
}
