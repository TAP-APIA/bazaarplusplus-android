#if BPP_ANDROID
using BazaarPlusPlus.Infrastructure.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

// Source-shadow JNI shims only: device verification exercises the actual ART registration.
namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    internal sealed class Il2CppReferenceArray<T>(T[] items)
    {
        internal T[] Items { get; } = items;
    }

    internal sealed class Il2CppStructArray<T>
    {
        internal Il2CppStructArray(int count) => Items = new T[count];

        internal Il2CppStructArray(T[] items) => Items = items;

        internal T[] Items { get; }
    }
}

namespace UnityEngine
{
#pragma warning disable CS8981 // Mirrors Unity's JNI value type name.
    internal struct jvalue
    {
        public IntPtr l;
    }
#pragma warning restore CS8981

    internal sealed class JNINativeMethod
    {
        public string name = "";
        public string signature = "";
        public IntPtr fnPtr;
    }

    internal static class AndroidJNI
    {
        internal static readonly IntPtr TrustManagerClass = new(7);
        internal static readonly List<string> FoundClasses = new();
        internal static readonly List<IntPtr> Deleted = new();
        internal static string? ClassName;
        internal static IntPtr RegisteredClass;
        internal static JNINativeMethod[] RegisteredMethods = Array.Empty<JNINativeMethod>();
        internal static int RegistrationResult;
        internal static bool FailClassLookup;
        internal static bool FailRegistrationWithJavaException;
        internal static int ExceptionClears;
        private static bool _exception;

        internal static void Reset()
        {
            FoundClasses.Clear();
            Deleted.Clear();
            ClassName = null;
            RegisteredClass = IntPtr.Zero;
            RegisteredMethods = Array.Empty<JNINativeMethod>();
            RegistrationResult = 0;
            FailClassLookup = FailRegistrationWithJavaException = _exception = false;
            ExceptionClears = 0;
        }

        internal static IntPtr FindClass(string name)
        {
            FoundClasses.Add(name);
            return new IntPtr(1);
        }

        internal static IntPtr GetStaticMethodID(IntPtr type, string name, string signature) =>
            type == new IntPtr(1)
            && name == "currentApplication"
            && signature == "()Landroid/app/Application;"
                ? new IntPtr(101)
                : throw new InvalidOperationException("Unexpected static JNI method.");

        internal static IntPtr CallStaticObjectMethod(
            IntPtr type,
            IntPtr method,
            Il2CppStructArray<jvalue> args
        ) =>
            type == new IntPtr(1) && method == new IntPtr(101) && args.Items.Length == 0
                ? new IntPtr(2)
                : throw new InvalidOperationException("Unexpected static JNI call.");

        internal static IntPtr GetObjectClass(IntPtr instance) =>
            instance.ToInt64() switch
            {
                2 => new IntPtr(3),
                4 => new IntPtr(5),
                _ => throw new InvalidOperationException("Unexpected JNI instance."),
            };

        internal static IntPtr GetMethodID(IntPtr type, string name, string signature) =>
            (type.ToInt64(), name, signature) switch
            {
                (3, "getClassLoader", "()Ljava/lang/ClassLoader;") => new IntPtr(102),
                (5, "loadClass", "(Ljava/lang/String;)Ljava/lang/Class;") => new IntPtr(103),
                _ => throw new InvalidOperationException("Unexpected JNI method."),
            };

        internal static IntPtr NewStringUTF(string value)
        {
            ClassName = value;
            return new IntPtr(6);
        }

        internal static IntPtr CallObjectMethod(
            IntPtr instance,
            IntPtr method,
            Il2CppStructArray<jvalue> args
        )
        {
            if (instance == new IntPtr(2) && method == new IntPtr(102) && args.Items.Length == 0)
                return new IntPtr(4);
            if (
                instance != new IntPtr(4)
                || method != new IntPtr(103)
                || args.Items.Length != 1
                || args.Items[0].l != new IntPtr(6)
            )
                throw new InvalidOperationException("Unexpected JNI class-loader call.");
            if (!FailClassLookup)
                return TrustManagerClass;
            _exception = true;
            return IntPtr.Zero;
        }

        internal static int RegisterNatives(
            IntPtr type,
            Il2CppReferenceArray<JNINativeMethod> methods
        )
        {
            RegisteredClass = type;
            RegisteredMethods = methods.Items;
            _exception = FailRegistrationWithJavaException;
            return RegistrationResult;
        }

        internal static IntPtr ExceptionOccurred() => _exception ? new IntPtr(99) : IntPtr.Zero;

        internal static void ExceptionDescribe() { }

        internal static void ExceptionClear()
        {
            ExceptionClears++;
            _exception = false;
        }

        internal static void DeleteLocalRef(IntPtr value) => Deleted.Add(value);
    }
}

namespace BazaarPlusPlus.Infrastructure.Logging
{
    internal enum BppLogFeatureScope
    {
        Plugin,
    }

    internal sealed record BppLogEvent(BppLogFeatureScope Scope, string Id);
}

namespace BazaarPlusPlus.Infrastructure
{
    internal static class BppLog
    {
        internal static void InfoEvent(BppLogEvent log, params (string, object?)[] fields) { }

        internal static void WarnEvent(
            BppLogEvent log,
            Exception exception,
            params (string, object?)[] fields
        ) { }
    }
}
#endif
