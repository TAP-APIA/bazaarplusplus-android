#if BPP_ANDROID
#nullable enable
using System;
using UnityEngine;
using Api = BazaarPlusPlus.GameInterop.AndroidGameFullscreen.Api;

namespace BazaarPlusPlus.GameInterop;

internal static class AndroidHostClassLoader
{
    // Helpers in classes3.dex belong to the host Application loader, not Unity's fused loader.
    // The caller owns the returned local class reference; this adapter retains no global refs.
    internal static IntPtr LoadLocal(string binaryName)
    {
        if (!Api.ApplicationLoaderAvailable)
            throw new InvalidOperationException(
                "The Android host application loader is unavailable."
            );
        IntPtr application = IntPtr.Zero,
            loader = IntPtr.Zero,
            name = IntPtr.Zero,
            javaClass = IntPtr.Zero;
        try
        {
            application = AndroidJNI.CallStaticObjectMethod(
                Api.ActivityThreadClass,
                Api.ActivityThreadCurrentApplication,
                Api.EmptyArgs
            );
            Require(application, "currentApplication");
            loader = AndroidJNI.CallObjectMethod(
                application,
                Api.ContextGetClassLoader,
                Api.EmptyArgs
            );
            Require(loader, "application.getClassLoader");
            name = AndroidJNI.NewStringUTF(binaryName);
            Require(name, "helper class name");
            javaClass = AndroidJNI.CallObjectMethod(
                loader,
                Api.ClassLoaderLoadClass,
                Api.ObjectArgs(name)
            );
            Require(javaClass, "loadClass(" + binaryName + ")");
            var result = javaClass;
            javaClass = IntPtr.Zero;
            return result;
        }
        finally
        {
            Delete(javaClass);
            Delete(name);
            Delete(loader);
            Delete(application);
        }
    }

    private static void Require(IntPtr value, string operation)
    {
        AndroidGameFullscreen.ThrowIfJavaException(operation);
        if (value == IntPtr.Zero)
            throw new InvalidOperationException("Android host lookup returned null: " + operation);
    }

    private static void Delete(IntPtr local)
    {
        if (local != IntPtr.Zero)
            AndroidJNI.DeleteLocalRef(local);
    }
}
#endif
