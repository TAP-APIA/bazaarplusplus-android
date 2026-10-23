#nullable enable
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Cards.Item;
using BazaarPlusPlus.GameInterop.ItemBoardPreview;

namespace BazaarPlusPlus.GameInterop.MonsterBoardPreview;

internal static class NativeMonsterBoardItemMapper
{
    internal static List<TCardInstanceItem> Map(BppItemBoard board, string instanceIdPrefix) =>
        BppItemBoardPreviewMapper
            .Map(BppItemBoardSlotPlanner.Plan(board))
            .Where(card =>
                card.SocketId.HasValue
                && (int)card.SocketId.Value >= 0
                && (int)card.SocketId.Value + card.DisplaySpan <= 10
            )
            .Select(
                (card, index) =>
                    new TCardInstanceItem
                    {
                        TemplateId = card.TemplateId.ToNativeGuid(),
                        TemplateVersion = string.Empty,
                        InstanceId = $"{instanceIdPrefix}-{index}",
                        Tier = card.Tier,
                        SocketId = card.SocketId.ToNativeNullable(),
                        EnchantmentType = card.EnchantmentType.ToNativeNullable(),
                        Attributes =
                            card.Attributes == null
                                ? new Il2CppSystem.Collections.Generic.Dictionary<ECardAttributeType, int>()
                                : new Dictionary<ECardAttributeType, int>(card.Attributes).ToNativeDictionary(),
                    }
            )
            .ToList();
}
