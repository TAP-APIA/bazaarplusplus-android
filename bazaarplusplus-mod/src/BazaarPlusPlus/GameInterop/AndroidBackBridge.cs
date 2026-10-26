#if BPP_ANDROID
#nullable enable
using System;
using UnityEngine;
using BazaarPlusPlus.Infrastructure;
using Api = BazaarPlusPlus.GameInterop.AndroidGameFullscreen.Api;

namespace BazaarPlusPlus.GameInterop;

/// <summary>
/// Bridges Android 13+ system-back to the BPP overlay panel host. The Java callback is registered
/// only while a panel is open, so back outside panels keeps the game's own behavior.
/// </summary>
internal static class AndroidBackBridge
{
    private const string ClassName = "com.bepinex.android.bpp.BppBackBridge";

    private static IntPtr _class;
    private static IntPtr _install;
    private static IntPtr _setConsume;
    private static IntPtr _takeBackPress;
    private static IntPtr _uninstall;
    private static bool _failed;
    private static bool _installed;
    private static IntPtr _installedActivity;

    internal static void EnsureInstalled(IntPtr activity)
    {
        if (_failed || activity == IntPtr.Zero)
            return;
        if (_installed && AndroidJNI.IsSameObject(activity, _installedActivity))
            return;
        try
        {
            if (_class == IntPtr.Zero)
            {
                var local = AndroidHostClassLoader.LoadLocal(ClassName);
                _class = AndroidJNI.NewGlobalRef(local);
                Delete(local);
                Require(_class);
                _install = Require(
                    AndroidJNI.GetStaticMethodID(_class, "install", "(Landroid/app/Activity;)V")
                );
                _setConsume = Require(AndroidJNI.GetStaticMethodID(_class, "setConsume", "(Z)V"));
                _takeBackPress = Require(
                    AndroidJNI.GetStaticMethodID(_class, "takeBackPress", "()Z")
                );
                _uninstall = Require(AndroidJNI.GetStaticMethodID(_class, "uninstall", "()V"));
            }
            AndroidJNI.CallStaticVoidMethod(_class, _install, Api.ObjectArgs(activity));
            if (Pending())
                return;
            _installed = true;
            if (_installedActivity != IntPtr.Zero)
                AndroidJNI.DeleteGlobalRef(_installedActivity);
            _installedActivity = Require(AndroidJNI.NewGlobalRef(activity));
            BppStartupTrace.Write("back-bridge:installed");
        }
        catch (Exception exception)
        {
            AndroidGameFullscreen.ClearJavaException();
            _failed = true;
            BppStartupTrace.Write("back-bridge:ensure-failed " + exception.Message);
        }
    }

    internal static void SetConsume(bool consume)
    {
        if (_failed || _class == IntPtr.Zero)
            return;
        try
        {
            AndroidJNI.CallStaticVoidMethod(_class, _setConsume, Api.BoolArgs(consume));
            if (Pending())
                return;
        }
        catch
        {
            AndroidGameFullscreen.ClearJavaException();
        }
    }

    internal static bool TakeBackPress()
    {
        if (_failed || _class == IntPtr.Zero)
            return false;
        try
        {
            var pressed = AndroidJNI.CallStaticBooleanMethod(_class, _takeBackPress, Api.EmptyArgs);
            return !Pending() && pressed;
        }
        catch
        {
            AndroidGameFullscreen.ClearJavaException();
            return false;
        }
    }

    internal static void Reset()
    {
        try
        {
            if (_class != IntPtr.Zero && _uninstall != IntPtr.Zero)
                AndroidJNI.CallStaticVoidMethod(_class, _uninstall, Api.EmptyArgs);
            AndroidGameFullscreen.ClearJavaException();
            if (_installedActivity != IntPtr.Zero)
                AndroidJNI.DeleteGlobalRef(_installedActivity);
            if (_class != IntPtr.Zero)
                AndroidJNI.DeleteGlobalRef(_class);
        }
        catch
        {
            AndroidGameFullscreen.ClearJavaException();
        }
        _class = IntPtr.Zero;
        _install = IntPtr.Zero;
        _setConsume = IntPtr.Zero;
        _takeBackPress = IntPtr.Zero;
        _uninstall = IntPtr.Zero;
        _failed = false;
        _installed = false;
        _installedActivity = IntPtr.Zero;
    }

    private static IntPtr Require(IntPtr value)
    {
        var pending = Pending();
        if (value == IntPtr.Zero || pending)
            throw new InvalidOperationException("Android Back helper lookup failed.");
        return value;
    }

    private static bool Pending()
    {
        var throwable = AndroidJNI.ExceptionOccurred();
        if (throwable == IntPtr.Zero)
            return false;
        AndroidJNI.ExceptionClear();
        Delete(throwable);
        return true;
    }

    private static void Delete(IntPtr local)
    {
        if (local == IntPtr.Zero)
            return;
        try
        {
            AndroidJNI.DeleteLocalRef(local);
        }
        catch
        {
            // A failed lookup must not throw during plugin setup.
        }
    }
}
#endif
