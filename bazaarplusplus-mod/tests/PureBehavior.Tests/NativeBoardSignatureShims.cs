using BazaarGameShared.Domain.Core.Types;
#if BPP_ANDROID
using System.Runtime.InteropServices;
using BazaarGameShared.Domain.Cards.Item;
#endif

// Source-shadow boundary: the Android generated nullable getters throw before conversion.
namespace BazaarGameShared.Domain.Core.Types
{
    public enum EEnchantmentType { Zero, Other }
    public enum EContainerSocketId { Zero, Other }
}

namespace BazaarGameShared.Domain.Cards
{
    public class TCardInstance
    {
        public Guid TemplateId { get; set; }
        public int Tier { get; set; }
        public Dictionary<string, int>? Attributes { get; set; }
    }
}

namespace BazaarGameShared.Domain.Cards.Item
{
    public sealed class TCardInstanceItem : TCardInstance
    {
        public EEnchantmentType? NativeEnchantment { get; set; }
        public EContainerSocketId? NativeSocket { get; set; }
#if BPP_ANDROID
        public EEnchantmentType? EnchantmentType => throw new InvalidOperationException("Generated nullable getter used.");
        public EContainerSocketId? SocketId => throw new InvalidOperationException("Generated nullable getter used.");
#else
        public EEnchantmentType? EnchantmentType => NativeEnchantment;
        public EContainerSocketId? SocketId => NativeSocket;
#endif
    }
}

internal static class NativeBoardSignatureShimExtensions
{
    internal static T? AsIl2Cpp<T>(this object value) where T : class => value as T;
    internal static T? ToManagedNullable<T>(this T? value) where T : struct => value;
    internal static Dictionary<string, int> ToManagedDictionary(this Dictionary<string, int> value) => value;
}

#if BPP_ANDROID
namespace Il2CppInterop.Runtime
{
    internal static class Il2CppClassPointerStore<T>
    {
        internal static IntPtr NativeClassPtr = typeof(T) == typeof(TCardInstanceItem) ? new(1)
            : typeof(T) == typeof(EEnchantmentType) ? new(2) : new(3);
    }

    internal static class IL2CPP
    {
        private static TCardInstanceItem? _current;
        internal static bool WrongBoxType;
        internal static bool NativeException;
        private static IntPtr _boxClass;
        // One boxed enum payload reused by this single-threaded source-shadow fixture.
        private static readonly IntPtr Payload = Marshal.AllocHGlobal(sizeof(int));

        internal static IntPtr Il2CppObjectBaseToPtr(TCardInstanceItem item)
        {
            _current = item;
            return new(10);
        }

        internal static IntPtr il2cpp_class_get_method_from_name(IntPtr declaringClass, string name, int count)
        {
            if (declaringClass != Il2CppClassPointerStore<TCardInstanceItem>.NativeClassPtr || count != 0)
                throw new InvalidOperationException("Wrong native getter owner.");
            return name switch { "get_EnchantmentType" => new(11), "get_SocketId" => new(12), _ => IntPtr.Zero };
        }

        internal static unsafe IntPtr il2cpp_runtime_invoke(IntPtr getter, IntPtr instance, void** args, ref IntPtr exception)
        {
            if (instance != new IntPtr(10) || args != null)
                throw new InvalidOperationException("Wrong native invocation.");
            exception = NativeException ? new(99) : IntPtr.Zero;
            var enchantment = getter == new IntPtr(11);
            int? value = enchantment ? (int?)_current!.NativeEnchantment : (int?)_current!.NativeSocket;
            _boxClass = WrongBoxType ? new(99) : enchantment
                ? Il2CppClassPointerStore<EEnchantmentType>.NativeClassPtr
                : Il2CppClassPointerStore<EContainerSocketId>.NativeClassPtr;
            if (!value.HasValue)
                return IntPtr.Zero;
            Marshal.WriteInt32(Payload, value.Value);
            return Payload;
        }

        internal static IntPtr il2cpp_object_get_class(IntPtr boxed) => _boxClass;
        internal static IntPtr il2cpp_object_unbox(IntPtr boxed) => boxed;
    }

    internal static class Il2CppException
    {
        internal static void RaiseExceptionIfNecessary(IntPtr exception)
        {
            if (exception != IntPtr.Zero)
                throw new InvalidOperationException("Native getter failed.");
        }
    }
}
#endif
