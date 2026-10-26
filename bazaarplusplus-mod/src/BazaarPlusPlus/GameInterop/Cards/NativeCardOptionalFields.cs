#nullable enable
using BazaarGameClient.Domain.Models.Cards;
using BazaarGameShared.Domain.Core.Types;
using BazaarPlusPlus.Infrastructure;
#if BPP_ANDROID
using System.Collections.Concurrent;
using Il2CppInterop.Runtime;
#endif

namespace BazaarPlusPlus.GameInterop.Cards;

/// <summary>
/// Reads the game's nullable card properties without going through the generated IL2CPP proxy.
///
/// On IL2CPP every generated <c>Nullable&lt;T&gt;</c> getter wraps the invoke result in
/// <c>new Nullable&lt;T&gt;(IntPtr.Zero)</c> when the native nullable is empty, and that
/// constructor throws <see cref="System.NullReferenceException"/>. The readers below resolve the
/// getter from the declaring native class, invoke it against the object pointer, and unbox only a
/// non-null result, so an empty nullable becomes a managed <c>null</c>.
///
/// Method ownership follows the game's declarations and is not interchangeable:
/// <c>get_Enchantment</c> lives on <c>ItemCard</c>, while <c>get_Section</c> and
/// <c>get_LeftSocketId</c> are virtual members of <c>Card</c>.
/// </summary>
internal static class NativeCardOptionalFields
{
    internal static EEnchantmentType? Enchantment(Card? card)
    {
        var item = card.AsIl2Cpp<ItemCard>();
        if (item == null)
            return null;
#if BPP_ANDROID
        return ReadAndroid<EEnchantmentType>(
            item,
            "get_Enchantment",
            Il2CppClassPointerStore<ItemCard>.NativeClassPtr
        );
#else
        return item.Enchantment.ToManagedNullable();
#endif
    }

    internal static EInventorySection? Section(Card? card)
    {
        if (card == null)
            return null;
#if BPP_ANDROID
        return ReadAndroid<EInventorySection>(
            card,
            "get_Section",
            Il2CppClassPointerStore<Card>.NativeClassPtr
        );
#else
        return card.Section.ToManagedNullable();
#endif
    }

    internal static EContainerSocketId? LeftSocketId(Card? card)
    {
        if (card == null)
            return null;
#if BPP_ANDROID
        return ReadAndroid<EContainerSocketId>(
            card,
            "get_LeftSocketId",
            Il2CppClassPointerStore<Card>.NativeClassPtr
        );
#else
        return card.LeftSocketId.ToManagedNullable();
#endif
    }

#if BPP_ANDROID
    /// <summary>
    /// Resolved getters keyed by declaring class and name. Callers run on the main thread; the
    /// concurrent map only keeps the cache from tearing if a future caller reads off-thread.
    /// </summary>
    private static readonly ConcurrentDictionary<
        (IntPtr DeclaringClass, string Name),
        IntPtr
    > Getters = new();

    private static unsafe T? ReadAndroid<T>(Card card, string name, IntPtr declaringClass)
        where T : struct
    {
        if (declaringClass == IntPtr.Zero)
            throw new InvalidOperationException(
                $"The native class that declares {name} is not initialised."
            );

        var instance = IL2CPP.Il2CppObjectBaseToPtr(card);
        if (instance == IntPtr.Zero)
            return null;

        var getter = ResolveGetter(declaringClass, name);
        // The properties are virtual. Resolve the implementation for the instance's runtime class
        // first, matching what the generated proxies do with il2cpp_object_get_virtual_method.
        var target = IL2CPP.il2cpp_object_get_virtual_method(instance, getter);
        if (target == IntPtr.Zero)
            target = getter;

        IntPtr exception = IntPtr.Zero;
        var boxed = IL2CPP.il2cpp_runtime_invoke(target, instance, null, ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
        return NativeNullableResult.Read<T>(boxed, Unbox<T>);
    }

    private static IntPtr ResolveGetter(IntPtr declaringClass, string name)
    {
        var key = (declaringClass, name);
        if (Getters.TryGetValue(key, out var cached))
            return cached;

        var getter = IL2CPP.il2cpp_class_get_method_from_name(declaringClass, name, 0);
        if (getter == IntPtr.Zero)
            throw new MissingMethodException(
                $"The native card type has no zero-argument {name} method."
            );

        // A concurrent first resolution produces the same native MethodInfo*; last writer wins.
        Getters[key] = getter;
        return getter;
    }

    private static unsafe T Unbox<T>(IntPtr boxed)
        where T : struct
    {
        var actual = IL2CPP.il2cpp_object_get_class(boxed);
        var expected = Il2CppClassPointerStore<T>.NativeClassPtr;
        if (actual != expected)
            throw new InvalidOperationException(
                $"A native nullable getter boxed {IL2CPP.il2cpp_class_get_name_(actual)} "
                    + $"instead of {typeof(T).FullName}."
            );

        return System.Runtime.CompilerServices.Unsafe.Read<T>(
            (void*)IL2CPP.il2cpp_object_unbox(boxed)
        );
    }
#endif
}
