using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Cards.Item;
using BazaarGameShared.Domain.Core.Types;
using BazaarPlusPlus.GameInterop.MonsterBoardPreview;
using Xunit;

public sealed class NativeBoardSignatureTests
{
    [Fact]
    public void Empty_board_and_unenchanted_unsocketed_item_do_not_abort_native_board_creation()
    {
        Assert.Equal(string.Empty, NativeBoardSignature.For(Array.Empty<TCardInstance>()));
        var item = new TCardInstanceItem();
        Assert.Null(NativeBoardItemOptionalFields.Enchantment(item));
        Assert.Null(NativeBoardItemOptionalFields.Socket(item));
        Assert.EndsWith(":0::", NativeBoardSignature.For([item]));
    }

    [Fact]
    public void Signature_keeps_present_zero_socket_and_enchantment_distinct_from_absence()
    {
        var item = new TCardInstanceItem();
        var empty = NativeBoardSignature.For([item]);
        item.NativeSocket = EContainerSocketId.Zero;
        item.NativeEnchantment = EEnchantmentType.Zero;
        Assert.Equal(EContainerSocketId.Zero, NativeBoardItemOptionalFields.Socket(item));
        Assert.Equal(EEnchantmentType.Zero, NativeBoardItemOptionalFields.Enchantment(item));
        Assert.NotEqual(empty, NativeBoardSignature.For([item]));
        var zero = NativeBoardSignature.For([item]);
        item.NativeEnchantment = EEnchantmentType.Other;
        Assert.NotEqual(zero, NativeBoardSignature.For([item]));
        var enchantment = NativeBoardSignature.For([item]);
        item.NativeSocket = EContainerSocketId.Other;
        Assert.NotEqual(enchantment, NativeBoardSignature.For([item]));
    }

    [Fact]
    public void Attributes_stay_order_independent_and_still_invalidate_signature()
    {
        var card = new TCardInstance { Attributes = new() { ["b"] = 2, ["a"] = 1 } };
        var signature = NativeBoardSignature.For([card]);
        card.Attributes = new() { ["a"] = 1, ["b"] = 2 };
        Assert.Equal(signature, NativeBoardSignature.For([card]));
        card.Attributes["a"] = 3;
        Assert.NotEqual(signature, NativeBoardSignature.For([card]));
    }

#if BPP_ANDROID
    [Fact]
    public void Native_errors_and_wrong_box_types_are_not_hidden_as_empty_fields()
    {
        var item = new TCardInstanceItem { NativeEnchantment = EEnchantmentType.Zero };
        try
        {
            Il2CppInterop.Runtime.IL2CPP.WrongBoxType = true;
            Assert.Throws<InvalidOperationException>(() => NativeBoardSignature.For([item]));
            Il2CppInterop.Runtime.IL2CPP.WrongBoxType = false;
            Il2CppInterop.Runtime.IL2CPP.NativeException = true;
            Assert.Throws<InvalidOperationException>(() => NativeBoardSignature.For([item]));
        }
        finally
        {
            Il2CppInterop.Runtime.IL2CPP.WrongBoxType = false;
            Il2CppInterop.Runtime.IL2CPP.NativeException = false;
        }
    }
#endif
}
