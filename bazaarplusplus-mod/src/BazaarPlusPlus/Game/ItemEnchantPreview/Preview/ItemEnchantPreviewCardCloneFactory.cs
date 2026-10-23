#nullable enable
using BazaarGameClient.Domain.Models.Cards;

namespace BazaarPlusPlus.Game.ItemEnchantPreview.Preview;

public static class ItemEnchantPreviewCardCloneFactory
{
    public static ItemCard Create(ItemCard source, ItemEnchantPreviewSnapshot snapshot)
    {
        return new ItemCard
        {
            InstanceId = source.InstanceId,
            TemplateId = source.TemplateId,
            Attributes = new Dictionary<BazaarGameShared.Domain.Core.Types.ECardAttributeType, int>(
                snapshot.PreviewAttributes
            ).ToNativeDictionary(),
            Heroes = source.Heroes,
            HiddenTags = source.HiddenTags,
            Size = source.Size,
            Tags = source.Tags,
            Tier = source.Tier,
            Type = source.Type,
            Owner = source.Owner,
            LeftSocketId = source.LeftSocketId,
            Section = source.Section,
            State = source.State,
            Template = source.Template,
            Enchantment = snapshot.PreviewEnchantment.ToNativeNullable(),
        };
    }
}
