#nullable enable
using System;
using System.Reflection;
using BazaarGameShared;

namespace BazaarPlusPlus.GameInterop;

/// <summary>
/// Calls the game's MessagePack configuration without naming <c>MessagePackSerializerOptions</c>.
///
/// The game's MessagePack assembly is unsigned while the NuGet copy this port compiles against is
/// signed, so the two identities differ and any signature that mentions a MessagePack type fails
/// with CS0012. Resolving the serializer through the options object's own assembly keeps the call
/// on the game's copy at runtime and keeps this file free of the type reference at compile time.
/// </summary>
internal static class BppMessagePack
{
    private static readonly Lazy<object?> OptionsAccessor = new(() =>
        typeof(MessagePackConfig)
            .GetProperty("Options", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null)
    );

    private static object? Options => OptionsAccessor.Value;

    public static byte[] Serialize<T>(T value)
    {
#if BPP_ANDROID
        var method = ResolveAndroid(serialize: true);
        var bytes = (Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>)
            method.MakeGenericMethod(typeof(T)).Invoke(null,
                new object?[] { value, Options, new Il2CppSystem.Threading.CancellationToken() })!;
        var result = new byte[bytes.Length];
        for (var index = 0; index < result.Length; index++)
            result[index] = bytes[index];
        return result;
#else
        var method = Resolve("Serialize");
        return (byte[])
            method.MakeGenericMethod(typeof(T)).Invoke(null, new object?[] { value, Options })!;
#endif
    }

    public static T Deserialize<T>(byte[] payload)
    {
#if BPP_ANDROID
        var method = ResolveAndroid(serialize: false);
        var stream = new Il2CppSystem.IO.MemoryStream(payload);
        try
        {
            return (T)method.MakeGenericMethod(typeof(T)).Invoke(null,
                new object?[] { stream, Options, new Il2CppSystem.Threading.CancellationToken() })!;
        }
        finally { stream.Close(); }
#else
        var method = Resolve("Deserialize");
        return (T)method.MakeGenericMethod(typeof(T)).Invoke(null, new object?[] { payload, Options })!;
#endif
    }

#if BPP_ANDROID
    // Native game messages stay on the game's AOT serializer and its registered formatters.
    // The Android proxy exposes a third CancellationToken and native arrays/streams.
    private static MethodInfo ResolveAndroid(bool serialize)
    {
        var options = Options ?? throw new InvalidOperationException("Native MessagePack options are unavailable.");
        var serializer = options.GetType().Assembly.GetType("MessagePack.MessagePackSerializer")
            ?? throw new InvalidOperationException("Native MessagePack serializer is unavailable.");
        return serializer.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(method =>
        {
            if (method.Name != (serialize ? "Serialize" : "Deserialize") || !method.IsGenericMethodDefinition)
                return false;
            var parameters = method.GetParameters();
            return parameters.Length == 3
                && parameters[1].ParameterType == options.GetType()
                && parameters[2].ParameterType == typeof(Il2CppSystem.Threading.CancellationToken)
                && (serialize ? parameters[0].ParameterType.IsGenericParameter
                    : parameters[0].ParameterType == typeof(Il2CppSystem.IO.Stream));
        });
    }
#endif

    private static MethodInfo Resolve(string name)
    {
        var options =
            Options
            ?? throw new InvalidOperationException(
                "MessagePackConfig.Options is unavailable; the game's MessagePack configuration has not loaded."
            );

        var optionsType = options.GetType();
        var serializer =
            optionsType.Assembly.GetType("MessagePack.MessagePackSerializer")
            ?? throw new InvalidOperationException(
                "MessagePackSerializer was not found in the game's MessagePack assembly."
            );

        foreach (var method in serializer.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name != name || !method.IsGenericMethodDefinition)
                continue;

            var parameters = method.GetParameters();
            if (parameters.Length == 2 && parameters[1].ParameterType == optionsType)
                return method;
        }

        throw new InvalidOperationException(
            $"MessagePackSerializer.{name}(T, options) was not found on {serializer.FullName}."
        );
    }
}
