#if BPP_ANDROID
using BazaarPlusPlus.GameInterop;
using UnityEngine;
using Xunit;

public sealed class AndroidTlsCertificateBridgeTests
{
    public AndroidTlsCertificateBridgeTests() => AndroidJNI.Reset();

    [Fact]
    public void Repeated_segments_identify_one_loaded_backend()
    {
        const string path =
            "/data/data/launcher/files/dotnet/libSystem.Security.Cryptography.Native.Android.so";
        var actual = AndroidTlsCertificateBridge.ResolveBackendPath(
            new[] { "1000-2000 r-xp 0 0 0 " + path, "2000-3000 r--p 0 0 0 " + path, "[vdso]" }
        );
        Assert.Equal(path, actual);
    }

    [Fact]
    public void Missing_or_ambiguous_backends_fail_closed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AndroidTlsCertificateBridge.ResolveBackendPath(Array.Empty<string>())
        );
        Assert.Throws<InvalidOperationException>(() =>
            AndroidTlsCertificateBridge.ResolveBackendPath(
                new[]
                {
                    "/apk/libSystem.Security.Cryptography.Native.Android.so",
                    "/dotnet/libSystem.Security.Cryptography.Native.Android.so",
                }
            )
        );
    }

    [Fact]
    public void Registers_the_original_pointer_on_the_application_loader_class()
    {
        var originalCallback = new IntPtr(0x123456);
        AndroidTlsCertificateBridge.RegisterCertificateCallback(originalCallback);

        Assert.Equal("android/app/ActivityThread", Assert.Single(AndroidJNI.FoundClasses));
        Assert.Equal("net.dot.android.crypto.DotnetProxyTrustManager", AndroidJNI.ClassName);
        Assert.Equal(AndroidJNI.TrustManagerClass, AndroidJNI.RegisteredClass);
        var method = Assert.Single(AndroidJNI.RegisteredMethods);
        Assert.Equal("verifyRemoteCertificate", method.name);
        Assert.Equal("(J)Z", method.signature);
        Assert.Equal(originalCallback, method.fnPtr);
        Assert.Equal(
            Enumerable.Range(1, 7).Select(n => new IntPtr(n)).Order(),
            AndroidJNI.Deleted.Order()
        );
        Assert.Equal(0, AndroidJNI.ExceptionClears);
    }

    [Fact]
    public void Missing_native_pointer_is_not_registered()
    {
        Assert.Throws<ArgumentException>(() =>
            AndroidTlsCertificateBridge.RegisterCertificateCallback(IntPtr.Zero)
        );
        Assert.Empty(AndroidJNI.FoundClasses);
        Assert.Empty(AndroidJNI.RegisteredMethods);
    }

    [Fact]
    public void Class_lookup_failure_clears_java_exception_and_releases_locals()
    {
        AndroidJNI.FailClassLookup = true;
        Assert.Throws<InvalidOperationException>(() =>
            AndroidTlsCertificateBridge.RegisterCertificateCallback(new IntPtr(123))
        );
        Assert.Empty(AndroidJNI.RegisteredMethods);
        Assert.Equal(
            Enumerable.Range(1, 6).Select(n => new IntPtr(n)).Order(),
            AndroidJNI.Deleted.Order()
        );
        Assert.Equal(1, AndroidJNI.ExceptionClears);
        Assert.Equal(IntPtr.Zero, AndroidJNI.ExceptionOccurred());
    }

    [Fact]
    public void Registration_failure_releases_the_helper_class_and_fails_closed()
    {
        AndroidJNI.RegistrationResult = -1;
        Assert.Throws<InvalidOperationException>(() =>
            AndroidTlsCertificateBridge.RegisterCertificateCallback(new IntPtr(123))
        );
        Assert.Equal(7, AndroidJNI.Deleted.Count);
        Assert.Single(AndroidJNI.Deleted, pointer => pointer == AndroidJNI.TrustManagerClass);
        Assert.Equal(1, AndroidJNI.ExceptionClears);
    }

    [Fact]
    public void Pending_java_exception_is_failure_even_if_register_returns_zero()
    {
        AndroidJNI.FailRegistrationWithJavaException = true;
        Assert.Throws<InvalidOperationException>(() =>
            AndroidTlsCertificateBridge.RegisterCertificateCallback(new IntPtr(123))
        );
        Assert.Equal(7, AndroidJNI.Deleted.Count);
        Assert.Equal(IntPtr.Zero, AndroidJNI.ExceptionOccurred());
    }
}
#endif
