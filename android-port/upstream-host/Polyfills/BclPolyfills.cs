using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace System;

/// <summary>
/// BCL members newer than .NET 6 that upstream calls, as C# 14 static extension members, so the
/// upstream call sites compile unchanged against the launcher's .NET 6 runtime. Each one mirrors
/// the real member's behavior; add one when a net6 build of upstream reports it missing.
/// </summary>
internal static class BclPolyfills
{
    extension(ArgumentException)
    {
        public static void ThrowIfNullOrEmpty(
            [NotNull] string? argument,
            [CallerArgumentExpression(nameof(argument))] string? paramName = null
        )
        {
            ArgumentNullException.ThrowIfNull(argument, paramName);
            if (argument.Length == 0)
                throw new ArgumentException("The value cannot be an empty string.", paramName);
        }

        public static void ThrowIfNullOrWhiteSpace(
            [NotNull] string? argument,
            [CallerArgumentExpression(nameof(argument))] string? paramName = null
        )
        {
            ArgumentNullException.ThrowIfNull(argument, paramName);
            if (string.IsNullOrWhiteSpace(argument))
                throw new ArgumentException(
                    "The value cannot be an empty string or composed entirely of whitespace.",
                    paramName
                );
        }
    }

    extension(ArgumentOutOfRangeException)
    {
        public static void ThrowIfNegative(
            int value,
            [CallerArgumentExpression(nameof(value))] string? paramName = null
        )
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(paramName, value, "The value must be non-negative.");
        }

        public static void ThrowIfNegative(
            long value,
            [CallerArgumentExpression(nameof(value))] string? paramName = null
        )
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(paramName, value, "The value must be non-negative.");
        }

        public static void ThrowIfNegativeOrZero(
            int value,
            [CallerArgumentExpression(nameof(value))] string? paramName = null
        )
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(paramName, value, "The value must be positive.");
        }

        public static void ThrowIfNegativeOrZero(
            long value,
            [CallerArgumentExpression(nameof(value))] string? paramName = null
        )
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(paramName, value, "The value must be positive.");
        }
    }

    extension(ObjectDisposedException)
    {
        public static void ThrowIf([DoesNotReturnIf(true)] bool condition, object instance)
        {
            if (condition)
                throw new ObjectDisposedException(instance?.GetType().FullName);
        }
    }

    extension(Convert)
    {
        public static string ToHexStringLower(byte[] inArray) =>
            Convert.ToHexString(inArray).ToLowerInvariant();

        public static string ToHexStringLower(ReadOnlySpan<byte> bytes) =>
            Convert.ToHexString(bytes).ToLowerInvariant();
    }

    extension(Stopwatch)
    {
        public static TimeSpan GetElapsedTime(long startingTimestamp) =>
            TimeSpan.FromTicks(
                (long)(
                    (Stopwatch.GetTimestamp() - startingTimestamp)
                    * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)
                )
            );
    }

    extension(SHA256)
    {
        public static byte[] HashData(Stream source)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(source);
        }
    }
}
