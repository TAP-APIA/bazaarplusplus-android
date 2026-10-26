#nullable enable
using BazaarGameShared.Domain.Cards.Item;
using BazaarGameShared.Domain.Core.Types;
#if BPP_ANDROID
using BazaarPlusPlus.Infrastructure;
using Il2CppInterop.Runtime;
#endif

namespace BazaarPlusPlus.GameInterop.MonsterBoardPreview;

// TCardInstanceItem declares non-virtual nullable getters, unlike the live Card model.
// An empty result cannot pass through its generated Nullable<T>(IntPtr.Zero) proxy.
internal static class NativeBoardItemOptionalFields
{
    internal static EEnchantmentType? Enchantment(TCardInstanceItem item)
    {
#if BPP_ANDROID
        return ReadAndroid<EEnchantmentType>(item, "get_EnchantmentType");
#else
        return item.EnchantmentType.ToManagedNullable();
#endif
    }

    internal static EContainerSocketId? Socket(TCardInstanceItem item)
    {
#if BPP_ANDROID
        return ReadAndroid<EContainerSocketId>(item, "get_SocketId");
#else
        return item.SocketId.ToManagedNullable();
#endif
    }

#if BPP_ANDROID
    private static unsafe T? ReadAndroid<T>(TCardInstanceItem item, string name)
        where T : struct
    {
        var declaringClass = Il2CppClassPointerStore<TCardInstanceItem>.NativeClassPtr;
        if (declaringClass == IntPtr.Zero)
            throw new InvalidOperationException("Native board item class is not initialised.");
        var getter = IL2CPP.il2cpp_class_get_method_from_name(declaringClass, name, 0);
        if (getter == IntPtr.Zero)
            throw new MissingMethodException($"Native board item has no {name} getter.");
        IntPtr exception = IntPtr.Zero;
        var boxed = IL2CPP.il2cpp_runtime_invoke(
            getter,
            IL2CPP.Il2CppObjectBaseToPtr(item),
            null,
            ref exception
        );
        Il2CppException.RaiseExceptionIfNecessary(exception);
        return NativeNullableResult.Read<T>(boxed, Unbox<T>);
    }

    private static unsafe T Unbox<T>(IntPtr boxed)
        where T : struct
    {
        if (IL2CPP.il2cpp_object_get_class(boxed) != Il2CppClassPointerStore<T>.NativeClassPtr)
            throw new InvalidOperationException(
                $"Native board nullable did not box {typeof(T).FullName}."
            );
        return System.Runtime.CompilerServices.Unsafe.Read<T>((void*)IL2CPP.il2cpp_object_unbox(boxed));
    }
#endif
}
