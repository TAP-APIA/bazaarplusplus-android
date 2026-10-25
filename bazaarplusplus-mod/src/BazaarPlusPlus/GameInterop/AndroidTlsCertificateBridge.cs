#if BPP_ANDROID
#nullable enable
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BazaarPlusPlus.GameInterop;

internal static class AndroidTlsCertificateBridge
{
    private const string LibraryName = "libSystem.Security.Cryptography.Native.Android.so";
    private const string JavaClassName = "net.dot.android.crypto.DotnetProxyTrustManager";
    private const string NativeExport =
        "Java_net_dot_android_crypto_DotnetProxyTrustManager_verifyRemoteCertificate";

    // ART keeps this function pointer: its library must remain loaded for the process lifetime.
    private static IntPtr _library;
    private static bool _registered;

    internal static void Initialize()
    {
        if (_registered)
            return;
        try
        {
            // Resolve the same backend CoreCLR uses, not a second copy from the launcher's APK.
            // This forces its normal initialization without starting an HTTPS request.
            RandomNumberGenerator.Fill(new byte[1]);
            var path = ResolveBackendPath(File.ReadLines("/proc/self/maps"));
            _library = NativeLibrary.Load(path);
            var callback = NativeLibrary.GetExport(_library, NativeExport);
            // dlopen alone does not associate the crypto backend with this Java class's loader.
            // Bind the original .NET entry point; it still delegates to SslStream's normal
            // certificate-chain and hostname checks. Never substitute an accepting callback.
            RegisterCertificateCallback(callback);
            _registered = true;
            BppLog.InfoEvent(
                new BppLogEvent(BppLogFeatureScope.Plugin, "plugin.android.tls.registered"),
                ("native_path", path),
                ("certificate_validation", "dotnet_default")
            );
        }
        catch (Exception exception)
        {
            BppLog.WarnEvent(
                new BppLogEvent(BppLogFeatureScope.Plugin, "plugin.android.tls.unavailable"),
                exception,
                ("reason_code", "certificate_callback_registration_failed")
            );
        }
    }

    internal static string ResolveBackendPath(IEnumerable<string> mappings)
    {
        var paths = mappings
            .Select(line =>
                line.IndexOf('/') is var start && start >= 0 ? line[start..].Trim() : ""
            )
            .Where(path => path.EndsWith("/" + LibraryName, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return paths.Length == 1
            ? paths[0]
            : throw new InvalidOperationException(
                "Expected one loaded Android crypto backend; found " + paths.Length + "."
            );
    }

    internal static void RegisterCertificateCallback(IntPtr callback)
    {
        if (callback == IntPtr.Zero)
            throw new ArgumentException(
                "A native certificate callback is required.",
                nameof(callback)
            );
        var methods = new Il2CppReferenceArray<JNINativeMethod>(
            new[]
            {
                new JNINativeMethod
                {
                    name = "verifyRemoteCertificate",
                    signature = "(J)Z",
                    fnPtr = callback,
                },
            }
        );
        var javaClass = LoadTrustManagerClass();
        try
        {
            var result = AndroidJNI.RegisterNatives(javaClass, methods);
            if (result != 0 || AndroidJNI.ExceptionOccurred() != IntPtr.Zero)
            {
                AndroidJNI.ExceptionClear();
                throw new InvalidOperationException(
                    "Android TLS callback registration failed: " + result
                );
            }
        }
        finally
        {
            AndroidJNI.DeleteLocalRef(javaClass);
        }
    }

    private static IntPtr LoadTrustManagerClass()
    {
        // Unity's FindClass uses the fused game's loader, which cannot see the launcher's
        // net.dot.android helpers. Resolve the class through the actual Android application.
        // Use non-generic JNI entry points: AndroidJavaObject.Call<T> is not AOT-instantiated
        // for the helper return types in this IL2CPP build.
        var locals = new List<IntPtr>();
        var emptyArguments = new Il2CppStructArray<jvalue>(0);
        try
        {
            var thread = Local(AndroidJNI.FindClass("android/app/ActivityThread"));
            var current = Required(
                AndroidJNI.GetStaticMethodID(
                    thread,
                    "currentApplication",
                    "()Landroid/app/Application;"
                )
            );
            var application = Local(
                AndroidJNI.CallStaticObjectMethod(thread, current, emptyArguments)
            );
            var appClass = Local(AndroidJNI.GetObjectClass(application));
            var getLoader = Required(
                AndroidJNI.GetMethodID(appClass, "getClassLoader", "()Ljava/lang/ClassLoader;")
            );
            var loader = Local(AndroidJNI.CallObjectMethod(application, getLoader, emptyArguments));
            var loaderClass = Local(AndroidJNI.GetObjectClass(loader));
            var loadClass = Required(
                AndroidJNI.GetMethodID(
                    loaderClass,
                    "loadClass",
                    "(Ljava/lang/String;)Ljava/lang/Class;"
                )
            );
            var className = Local(AndroidJNI.NewStringUTF(JavaClassName));
            return Required(
                AndroidJNI.CallObjectMethod(
                    loader,
                    loadClass,
                    new Il2CppStructArray<jvalue>(new[] { new jvalue { l = className } })
                )
            );
        }
        finally
        {
            foreach (var local in locals.AsEnumerable().Reverse())
                AndroidJNI.DeleteLocalRef(local);
        }

        IntPtr Local(IntPtr value)
        {
            Required(value);
            locals.Add(value);
            return value;
        }
    }

    private static IntPtr Required(IntPtr value)
    {
        if (AndroidJNI.ExceptionOccurred() != IntPtr.Zero)
        {
            AndroidJNI.ExceptionDescribe();
            AndroidJNI.ExceptionClear();
            throw new InvalidOperationException(
                "Could not resolve the launcher's TLS helper class."
            );
        }
        return value != IntPtr.Zero
            ? value
            : throw new InvalidOperationException(
                "Android returned a null TLS helper class, object or method."
            );
    }
}
#endif
