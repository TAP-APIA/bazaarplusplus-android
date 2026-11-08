using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
namespace BazaarHelper;
/** Native nullable values box as either null or their underlying enum, never Nullable proxies. */
internal static class OptionalValue
{
    private static readonly ConcurrentDictionary<(IntPtr, string), (IntPtr, bool)> Members = new();
    internal static unsafe T? Read<T>(Il2CppObjectBase? obj, string property) where T : unmanaged
    {
        if (obj == null)
            return null;
        var instance = obj.Pointer;
        var cls = IL2CPP.il2cpp_object_get_class(instance);
        var member = Members.GetOrAdd((cls, property), key => { var method = IL2CPP.il2cpp_class_get_method_from_name(key.Item1, "get_" + key.Item2, 0); if (method != IntPtr.Zero) return (method, false); var field = IL2CPP.il2cpp_class_get_field_from_name(key.Item1, key.Item2); if (field == IntPtr.Zero) throw new MissingMemberException(key.Item2); return (field, true); });
        IntPtr boxed;
        if (member.Item2)
            boxed = IL2CPP.il2cpp_field_get_value_object(member.Item1, instance);
        else
        {
            var exception = IntPtr.Zero;
            var method = IL2CPP.il2cpp_object_get_virtual_method(instance, member.Item1);
            boxed = IL2CPP.il2cpp_runtime_invoke(method == IntPtr.Zero ? member.Item1 : method, instance, null, ref exception);
            Il2CppException.RaiseExceptionIfNecessary(exception);
        }
        if (boxed == IntPtr.Zero)
            return null;
        var handle = IL2CPP.il2cpp_gchandle_new(boxed, false);
        try
        {
            if (IL2CPP.il2cpp_object_get_class(boxed) != Il2CppClassPointerStore<T>.NativeClassPtr)
                throw new InvalidDataException("Unexpected boxed enum");
            return Unsafe.Read<T>((void*)IL2CPP.il2cpp_object_unbox(boxed));
        }
        finally { IL2CPP.il2cpp_gchandle_free(handle); }
    }
    /** Same as Read, but a type without that member (a skill has no Enchantment) reads as absent instead of throwing. */
    internal static T? TryRead<T>(Il2CppObjectBase? obj, string property) where T : unmanaged
    {
        try { return Read<T>(obj, property); }
        catch (MissingMemberException) { return null; }
    }
}
