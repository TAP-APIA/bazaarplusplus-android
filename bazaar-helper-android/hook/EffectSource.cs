using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using BazaarGameShared.Domain.Core;
namespace BazaarHelper;

internal static class EffectSource
{
    internal static unsafe string? Read(Il2CppObjectBase effect)
    {
        var instance = effect.Pointer;
        var cls = IL2CPP.il2cpp_object_get_class(instance);
        var getter = IL2CPP.il2cpp_class_get_method_from_name(cls, "get_Source", 0);
        if (getter == IntPtr.Zero)
            return null;
        IntPtr exception = IntPtr.Zero;
        var boxed = IL2CPP.il2cpp_runtime_invoke(getter, instance, null, ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
        if (boxed == IntPtr.Zero)
            return null;
        var handle = IL2CPP.il2cpp_gchandle_new(boxed, false);
        try
        {
            var actual = IL2CPP.il2cpp_object_get_class(boxed);
            if (actual != Il2CppClassPointerStore<InstanceId>.NativeClassPtr)
                throw new InvalidDataException("Unexpected effect source");
            var value = IL2CPP.il2cpp_class_get_method_from_name(actual, "get_Value", 0);
            if (value == IntPtr.Zero)
                return null;
            var text = IL2CPP.il2cpp_runtime_invoke(value, IL2CPP.il2cpp_object_unbox(boxed), null, ref exception);
            Il2CppException.RaiseExceptionIfNecessary(exception);
            return text == IntPtr.Zero ? null : IL2CPP.Il2CppStringToManaged(text);
        }
        finally { IL2CPP.il2cpp_gchandle_free(handle); }
    }
}
