#nullable enable
namespace BazaarPlusPlus.Infrastructure;

/// <summary>
/// Reads a boxed native <c>Nullable&lt;T&gt;</c> without constructing the generated proxy.
///
/// The IL2CPP interop getter for a nullable property invokes the native method and then wraps the
/// result in <c>new Nullable&lt;T&gt;(pointer)</c> unconditionally. IL2CPP boxes a non-empty
/// nullable as its underlying <c>T</c> and an empty one as a null pointer (ECMA-335 boxing), and
/// <c>Il2CppObjectBase(IntPtr)</c> throws <see cref="System.NullReferenceException"/> for a zero
/// pointer. A direct property read therefore fails with
/// <c>Il2CppSystem.ValueType..ctor(IntPtr) -&gt; Nullable`1..ctor(IntPtr)</c> whenever the native
/// nullable is empty. Callers that reach the runtime through <c>il2cpp_runtime_invoke</c> pass the
/// raw result here instead.
/// </summary>
internal static class NativeNullableResult
{
    /// <summary>
    /// Returns <c>null</c> for the empty nullable, otherwise unboxes the boxed payload.
    /// <paramref name="unbox"/> must throw for a payload it does not recognise instead of
    /// reporting "no value", so a corrupt read cannot silently look like an unenchanted card.
    /// </summary>
    internal static T? Read<T>(IntPtr boxed, Func<IntPtr, T> unbox)
        where T : struct => boxed == IntPtr.Zero ? null : unbox(boxed);
}
