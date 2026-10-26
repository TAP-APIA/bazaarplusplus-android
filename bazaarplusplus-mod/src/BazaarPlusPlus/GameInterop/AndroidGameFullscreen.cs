#if BPP_ANDROID
#nullable enable
using System;
using System.Collections.Generic;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BazaarPlusPlus.GameInterop;

/// <summary>
/// Edge-to-edge immersive fullscreen for the Android game window.
///
/// The game runs as <c>com.bepinex.android:game</c> inside the launcher APK, so the window driven
/// here is the launcher's game activity, not a window owned by the game APK. The BPP launcher
/// activity runs in the default process and keeps its own status-bar insets; this class never
/// resolves or mutates it.
///
/// The launcher's Unity-activity onCreate hook
/// (<c>com.bepinex.android.BootstrapActivity</c>) hides the system bars once, but they can return
/// after focus or system-UI transitions. This
/// component discovers the game activity once per second and on focus/size changes. The host-loaded
/// Java helper owns window mutations on Android's UI thread, including supported cutout modes.
/// The binary manifest and its theme are deliberately left untouched.
///
/// JNI resolution and helper dispatch run on UnityMain, not Android's UI thread.
/// The generic managed wrappers (<c>AndroidJavaObject.Call&lt;T&gt;</c>) are avoided because this
/// IL2CPP build does not AOT-instantiate them for every return type.
/// </summary>
internal static class AndroidGameFullscreen
{
    private const string UnityPlayerClassName = "com/unity3d/player/UnityPlayer";
    private const string UnityPlayerBinaryName = "com.unity3d.player.UnityPlayer";
    private const string LauncherActivityName = "com.bepinex.android.bpp.BppLauncherActivity";
    private const string WindowHelperName = "com.bepinex.android.bpp.BppGameWindow";

    // Discovery/policy-repair heartbeat. Java ignores an unchanged immersive policy and handles
    // focus events; it must not hide transient swipe bars or redispatch insets on this cadence.
    internal const float ReapplyIntervalSeconds = 1f;

    // The activity is normally available before the first Update; a device that needs longer must
    // not turn a transient startup race into a warning.
    internal const int ActivityMissesBeforeWarning = 5;

    private static bool _warned;

    // The Java class is resolved lazily: UnityPlayer may not be loaded yet when the plugin starts.
    private static IntPtr _unityPlayerClass;
    private static IntPtr _unityPlayerCurrentActivity;

    internal static void Install()
    {
        if (_running)
        {
            _disabled = false;
            return;
        }
        _disabled = false;
        BppCoroutines.Start(new object(), Driver());
    }

    internal static void Reset()
    {
        _disabled = true;
        _queuedLogged = false;
        _nextApplyAt = 0f;
        _activityMisses = 0;
        _warned = false;
        ResetWindowHelper();
        ReleaseActivity();
        DeleteGlobalQuietly(_unityPlayerClass);
        _unityPlayerClass = IntPtr.Zero;
        _unityPlayerCurrentActivity = IntPtr.Zero;
        Api.Reset();
    }

    // One warning per install: a repeated JNI failure must not flood LogOutput.log every second.
    internal static void WarnOnce(string reasonCode, Exception? exception)
    {
        if (_warned)
            return;
        _warned = true;
        try
        {
            var logEvent = new BppLogEvent(
                BppLogFeatureScope.Plugin,
                "plugin.android.fullscreen.unavailable"
            );
            if (exception == null)
                BppLog.WarnEvent(logEvent, ("reason_code", reasonCode));
            else
                BppLog.WarnEvent(logEvent, exception, ("reason_code", reasonCode));
        }
        catch
        {
            // Logging must never throw into the game loop.
        }
    }

    // UnityEngine.UnityPlayer.currentActivity is a thin wrapper over this Java static field; the
    // launcher's Unity-activity onCreate hook sets it. Reading the field through AndroidJNI avoids
    // the IL2CPP-uninstantiated generic AndroidJavaObject.GetStatic<AndroidJavaObject>. FindClass
    // resolves through the fused game's class loader, which owns UnityPlayer in this process.
    private static bool EnsureUnityPlayerClass()
    {
        if (_unityPlayerClass != IntPtr.Zero)
            return true;
        try
        {
            var javaClass = AndroidJNI.FindClass(UnityPlayerClassName);
            var pending = JavaExceptionPending();
            if (javaClass == IntPtr.Zero)
                return false;
            if (pending)
            {
                AndroidJNI.DeleteLocalRef(javaClass);
                return false;
            }
            try
            {
                var field = AndroidJNI.GetStaticFieldID(
                    javaClass,
                    "currentActivity",
                    "Landroid/app/Activity;"
                );
                var fieldPending = JavaExceptionPending();
                if (field == IntPtr.Zero || fieldPending)
                    return false;
                var global = AndroidJNI.NewGlobalRef(javaClass);
                if (global == IntPtr.Zero)
                {
                    ClearJavaException();
                    return false;
                }
                _unityPlayerClass = global;
                _unityPlayerCurrentActivity = field;
                return true;
            }
            finally
            {
                AndroidJNI.DeleteLocalRef(javaClass);
            }
        }
        catch
        {
            ClearJavaException();
            return false;
        }
    }

    private static IntPtr ResolveUnityPlayerActivity()
    {
        if (!EnsureUnityPlayerClass())
            return IntPtr.Zero;
        var locals = new List<IntPtr>(1);
        try
        {
            var activity = AndroidJNI.GetStaticObjectField(
                _unityPlayerClass,
                _unityPlayerCurrentActivity
            );
            if (activity != IntPtr.Zero)
                locals.Add(activity);
            var pending = JavaExceptionPending();
            if (activity == IntPtr.Zero || pending)
                return IntPtr.Zero;
            return ToActivityGlobal(activity);
        }
        finally
        {
            DeleteLocals(locals);
        }
    }

    private static IntPtr ResolveUnityPlayerActivityFromApplicationLoader()
    {
        if (!Api.ApplicationLoaderAvailable)
            return IntPtr.Zero;
        var locals = new List<IntPtr>(4);
        try
        {
            var application = AndroidJNI.CallStaticObjectMethod(
                Api.ActivityThreadClass,
                Api.ActivityThreadCurrentApplication,
                Api.EmptyArgs
            );
            if (application != IntPtr.Zero)
                locals.Add(application);
            var pending = JavaExceptionPending();
            if (application == IntPtr.Zero || pending)
                return IntPtr.Zero;
            var loader = AndroidJNI.CallObjectMethod(
                application,
                Api.ContextGetClassLoader,
                Api.EmptyArgs
            );
            if (loader != IntPtr.Zero)
                locals.Add(loader);
            pending = JavaExceptionPending();
            if (loader == IntPtr.Zero || pending)
                return IntPtr.Zero;
            var className = AndroidJNI.NewStringUTF(UnityPlayerBinaryName);
            if (className == IntPtr.Zero)
            {
                ClearJavaException();
                return IntPtr.Zero;
            }
            locals.Add(className);
            var javaClass = AndroidJNI.CallObjectMethod(
                loader,
                Api.ClassLoaderLoadClass,
                Api.ObjectArgs(className)
            );
            if (javaClass != IntPtr.Zero)
                locals.Add(javaClass);
            pending = JavaExceptionPending();
            if (javaClass == IntPtr.Zero || pending)
                return IntPtr.Zero;
            var field = AndroidJNI.GetStaticFieldID(
                javaClass,
                "currentActivity",
                "Landroid/app/Activity;"
            );
            pending = JavaExceptionPending();
            if (field == IntPtr.Zero || pending)
                return IntPtr.Zero;
            var activity = AndroidJNI.GetStaticObjectField(javaClass, field);
            if (activity != IntPtr.Zero)
                locals.Add(activity);
            pending = JavaExceptionPending();
            if (activity == IntPtr.Zero || pending)
                return IntPtr.Zero;
            return ToActivityGlobal(activity);
        }
        finally
        {
            DeleteLocals(locals);
        }
    }

    // mActivities is a hidden field, so this path is the last resort; the primary UnityPlayer field
    // is set by the launcher before the game's first Activity is resumed.
    private static IntPtr ResolveActivityThreadActivity()
    {
        if (!Api.ActivityThreadScanAvailable)
            return IntPtr.Zero;
        var locals = new List<IntPtr>(8);
        try
        {
            var thread = AndroidJNI.CallStaticObjectMethod(
                Api.ActivityThreadClass,
                Api.ActivityThreadCurrent,
                Api.EmptyArgs
            );
            if (thread != IntPtr.Zero)
                locals.Add(thread);
            var pending = JavaExceptionPending();
            if (thread == IntPtr.Zero || pending)
                return IntPtr.Zero;
            var activities = AndroidJNI.GetObjectField(thread, Api.ActivityThreadActivities);
            if (activities != IntPtr.Zero)
                locals.Add(activities);
            pending = JavaExceptionPending();
            if (activities == IntPtr.Zero || pending)
                return IntPtr.Zero;
            var count = AndroidJNI.CallIntMethod(activities, Api.ArrayMapSize, Api.EmptyArgs);
            pending = JavaExceptionPending();
            if (pending || count is <= 0 or > 64)
                return IntPtr.Zero;
            IntPtr fallback = IntPtr.Zero;
            for (var index = 0; index < count; index++)
            {
                var record = AndroidJNI.CallObjectMethod(
                    activities,
                    Api.ArrayMapValueAt,
                    Api.IntArgs(index)
                );
                if (record != IntPtr.Zero)
                    locals.Add(record);
                pending = JavaExceptionPending();
                if (pending)
                    return IntPtr.Zero;
                if (record == IntPtr.Zero)
                    continue;
                var activity = AndroidJNI.GetObjectField(record, Api.ActivityClientRecordActivity);
                if (activity != IntPtr.Zero)
                    locals.Add(activity);
                pending = JavaExceptionPending();
                if (pending)
                    return IntPtr.Zero;
                if (activity == IntPtr.Zero)
                    continue;
                if (IsActivityGone(activity))
                    continue;
                if (fallback == IntPtr.Zero)
                    fallback = activity;
                var focused = AndroidJNI.CallBooleanMethod(
                    activity,
                    Api.ActivityHasWindowFocus,
                    Api.EmptyArgs
                );
                pending = JavaExceptionPending();
                if (pending)
                    return fallback == IntPtr.Zero ? IntPtr.Zero : ToActivityGlobal(fallback);
                if (focused)
                    return ToActivityGlobal(activity);
            }
            return fallback == IntPtr.Zero ? IntPtr.Zero : ToActivityGlobal(fallback);
        }
        finally
        {
            DeleteLocals(locals);
        }
    }

    internal static IntPtr ResolveActivity(out string source)
    {
        var activity = AcceptActivity(ResolveUnityPlayerActivity(), "unity_player", out source);
        if (activity != IntPtr.Zero)
            return activity;
        // Scanning ActivityThread also recovers the window when UnityPlayer.currentActivity has
        // not been published yet or FindClass cannot see the game's class loader.
        activity = AcceptActivity(ResolveActivityThreadActivity(), "activity_thread", out source);
        if (activity != IntPtr.Zero)
            return activity;
        activity = AcceptActivity(
            ResolveUnityPlayerActivityFromApplicationLoader(),
            "application_loader",
            out source
        );
        if (activity != IntPtr.Zero)
            return activity;
        source = string.Empty;
        return IntPtr.Zero;
    }

    // A stale UnityPlayer.currentActivity value must not shadow the next resolver: drop dead
    // activities and the BPP launcher, then let the caller try the next source.
    private static IntPtr AcceptActivity(IntPtr activity, string candidateSource, out string source)
    {
        source = string.Empty;
        if (activity == IntPtr.Zero)
            return IntPtr.Zero;
        if (!IsActivityGone(activity) && !IsLauncherActivity(activity))
        {
            source = candidateSource;
            return activity;
        }
        try
        {
            AndroidJNI.DeleteGlobalRef(activity);
        }
        catch
        {
            ClearJavaException();
        }
        return IntPtr.Zero;
    }

    private static IntPtr ToActivityGlobal(IntPtr local)
    {
        try
        {
            if (!AndroidJNI.IsInstanceOf(local, Api.ActivityClass))
                return IntPtr.Zero;
            var global = AndroidJNI.NewGlobalRef(local);
            if (global == IntPtr.Zero)
                ClearJavaException();
            return global;
        }
        catch
        {
            ClearJavaException();
            return IntPtr.Zero;
        }
    }

    private static bool IsLauncherActivity(IntPtr activity)
    {
        var locals = new List<IntPtr>(1);
        try
        {
            var javaClass = AndroidJNI.GetObjectClass(activity);
            if (javaClass != IntPtr.Zero)
                locals.Add(javaClass);
            var pending = JavaExceptionPending();
            if (javaClass == IntPtr.Zero || pending)
                return false;
            var name = AndroidJNI.CallStringMethod(javaClass, Api.ClassGetName, Api.EmptyArgs);
            if (JavaExceptionPending())
                return false;
            return string.Equals(name, LauncherActivityName, StringComparison.Ordinal);
        }
        catch
        {
            ClearJavaException();
            return false;
        }
        finally
        {
            DeleteLocals(locals);
        }
    }

    internal static bool IsActivityUsable(IntPtr activity) =>
        activity != IntPtr.Zero && !IsActivityGone(activity);

    private static bool IsActivityGone(IntPtr activity)
    {
        try
        {
            var finishing = AndroidJNI.CallBooleanMethod(
                activity,
                Api.ActivityIsFinishing,
                Api.EmptyArgs
            );
            if (JavaExceptionPending())
                return true;
            if (finishing)
                return true;
            var destroyed = AndroidJNI.CallBooleanMethod(
                activity,
                Api.ActivityIsDestroyed,
                Api.EmptyArgs
            );
            if (JavaExceptionPending())
                return true;
            return destroyed;
        }
        catch
        {
            ClearJavaException();
            return true;
        }
    }

    internal static void ThrowIfJavaException(string operation)
    {
        var throwable = AndroidJNI.ExceptionOccurred();
        if (throwable == IntPtr.Zero)
            return;
        AndroidJNI.ExceptionClear();
        DeleteLocalQuietly(throwable);
        throw new InvalidOperationException("Android rejected " + operation + ".");
    }

    // ExceptionOccurred returns a local reference; clear and release it so a per-second retry
    // cannot exhaust the thread's local-reference table.
    private static bool JavaExceptionPending()
    {
        var throwable = AndroidJNI.ExceptionOccurred();
        if (throwable == IntPtr.Zero)
            return false;
        AndroidJNI.ExceptionClear();
        DeleteLocalQuietly(throwable);
        return true;
    }

    internal static void ClearJavaException()
    {
        try
        {
            var throwable = AndroidJNI.ExceptionOccurred();
            if (throwable == IntPtr.Zero)
                return;
            AndroidJNI.ExceptionClear();
            DeleteLocalQuietly(throwable);
        }
        catch
        {
            // Nothing else can be done; the caller is already handling a failure.
        }
    }

    private static void DeleteLocalQuietly(IntPtr local)
    {
        try
        {
            AndroidJNI.DeleteLocalRef(local);
        }
        catch
        {
            // The exception reference is already unreachable.
        }
    }

    private static void DeleteGlobalQuietly(IntPtr global)
    {
        if (global == IntPtr.Zero)
            return;
        try
        {
            AndroidJNI.DeleteGlobalRef(global);
        }
        catch
        {
            ClearJavaException();
        }
    }

    internal static void DeleteLocals(List<IntPtr> locals)
    {
        for (var index = locals.Count - 1; index >= 0; index--)
        {
            var local = locals[index];
            if (local != IntPtr.Zero)
                AndroidJNI.DeleteLocalRef(local);
        }
        locals.Clear();
    }

    // Android activity-resolution surface. Window APIs live exclusively in the Java helper.
    internal static class Api
    {
        internal static readonly Il2CppStructArray<jvalue> EmptyArgs = new(0);

        private static bool _initialized;
        private static bool _failed;
        private static readonly List<IntPtr> Classes = new();

        internal static IntPtr ActivityClass { get; private set; }
        internal static IntPtr ActivityIsFinishing { get; private set; }
        internal static IntPtr ActivityIsDestroyed { get; private set; }
        internal static IntPtr ActivityHasWindowFocus { get; private set; }
        internal static IntPtr ClassGetName { get; private set; }
        internal static bool ApplicationLoaderAvailable { get; private set; }
        internal static IntPtr ActivityThreadClass { get; private set; }
        internal static IntPtr ActivityThreadCurrent { get; private set; }
        internal static IntPtr ActivityThreadCurrentApplication { get; private set; }
        internal static IntPtr ContextGetClassLoader { get; private set; }
        internal static IntPtr ClassLoaderLoadClass { get; private set; }
        internal static bool ActivityThreadScanAvailable { get; private set; }
        internal static IntPtr ActivityThreadActivities { get; private set; }
        internal static IntPtr ArrayMapSize { get; private set; }
        internal static IntPtr ArrayMapValueAt { get; private set; }
        internal static IntPtr ActivityClientRecordActivity { get; private set; }

        internal static bool TryInitialize(out Exception? error)
        {
            error = null;
            if (_initialized)
                return true;
            if (_failed)
            {
                error = new InvalidOperationException(
                    "The Android fullscreen JNI surface is unavailable."
                );
                return false;
            }
            try
            {
                InitializeCore();
                InitializeFallbacks();
                _initialized = true;
                return true;
            }
            catch (Exception exception)
            {
                ClearJavaException();
                _failed = true;
                error = exception;
                return false;
            }
        }

        internal static Il2CppStructArray<jvalue> IntArgs(int value) =>
            new(new[] { new jvalue { i = value } });

        internal static Il2CppStructArray<jvalue> BoolArgs(bool value) =>
            new(new[] { new jvalue { z = value } });

        internal static Il2CppStructArray<jvalue> ObjectArgs(IntPtr value) =>
            new(new[] { new jvalue { l = value } });

        private static void InitializeCore()
        {
            ActivityClass = FindClass("android/app/Activity");
            ActivityIsFinishing = Method(ActivityClass, "isFinishing", "()Z");
            ActivityIsDestroyed = Method(ActivityClass, "isDestroyed", "()Z");
            ClassGetName = Method(FindClass("java/lang/Class"), "getName", "()Ljava/lang/String;");
        }

        private static void InitializeFallbacks()
        {
            try
            {
                ActivityThreadClass = FindClass("android/app/ActivityThread");
                ActivityThreadCurrentApplication = StaticMethod(
                    ActivityThreadClass,
                    "currentApplication",
                    "()Landroid/app/Application;"
                );
                ContextGetClassLoader = Method(
                    FindClass("android/content/Context"),
                    "getClassLoader",
                    "()Ljava/lang/ClassLoader;"
                );
                ClassLoaderLoadClass = Method(
                    FindClass("java/lang/ClassLoader"),
                    "loadClass",
                    "(Ljava/lang/String;)Ljava/lang/Class;"
                );
                ApplicationLoaderAvailable = true;
            }
            catch
            {
                ClearJavaException();
                ApplicationLoaderAvailable = false;
            }

            try
            {
                if (ActivityThreadClass == IntPtr.Zero)
                    ActivityThreadClass = FindClass("android/app/ActivityThread");
                ActivityThreadCurrent = StaticMethod(
                    ActivityThreadClass,
                    "currentActivityThread",
                    "()Landroid/app/ActivityThread;"
                );
                ActivityThreadActivities = Field(
                    ActivityThreadClass,
                    "mActivities",
                    "Landroid/util/ArrayMap;",
                    staticField: false
                );
                ActivityHasWindowFocus = Method(ActivityClass, "hasWindowFocus", "()Z");
                var arrayMapClass = FindClass("android/util/ArrayMap");
                ArrayMapSize = Method(arrayMapClass, "size", "()I");
                ArrayMapValueAt = Method(arrayMapClass, "valueAt", "(I)Ljava/lang/Object;");
                ActivityClientRecordActivity = Field(
                    FindClass("android/app/ActivityThread$ActivityClientRecord"),
                    "activity",
                    "Landroid/app/Activity;",
                    staticField: false
                );
                ActivityThreadScanAvailable = true;
            }
            catch
            {
                ClearJavaException();
                ActivityThreadScanAvailable = false;
            }
        }

        private static IntPtr FindClass(string name)
        {
            var javaClass = AndroidJNI.FindClass(name);
            ThrowIfJavaException("FindClass(" + name + ")");
            if (javaClass == IntPtr.Zero)
                throw new InvalidOperationException("Android class not found: " + name);
            var global = AndroidJNI.NewGlobalRef(javaClass);
            AndroidJNI.DeleteLocalRef(javaClass);
            ThrowIfJavaException("NewGlobalRef(" + name + ")");
            if (global == IntPtr.Zero)
                throw new InvalidOperationException("Could not retain Android class: " + name);
            Classes.Add(global);
            return global;
        }

        private static IntPtr Method(IntPtr javaClass, string name, string signature) =>
            RequireMethod(AndroidJNI.GetMethodID(javaClass, name, signature), name);

        private static IntPtr StaticMethod(IntPtr javaClass, string name, string signature) =>
            RequireMethod(AndroidJNI.GetStaticMethodID(javaClass, name, signature), name);

        private static IntPtr Field(
            IntPtr javaClass,
            string name,
            string signature,
            bool staticField
        )
        {
            var field = staticField
                ? AndroidJNI.GetStaticFieldID(javaClass, name, signature)
                : AndroidJNI.GetFieldID(javaClass, name, signature);
            ThrowIfJavaException("field " + name);
            if (field == IntPtr.Zero)
                throw new InvalidOperationException("Android field not found: " + name);
            return field;
        }

        internal static void Reset()
        {
            foreach (var javaClass in Classes)
                DeleteGlobalQuietly(javaClass);
            Classes.Clear();
            _initialized = false;
            _failed = false;
            ApplicationLoaderAvailable = false;
            ActivityThreadScanAvailable = false;
            ActivityThreadClass = IntPtr.Zero;
            ActivityClass = IntPtr.Zero;
            ActivityIsFinishing = IntPtr.Zero;
            ActivityIsDestroyed = IntPtr.Zero;
            ActivityHasWindowFocus = IntPtr.Zero;
            ClassGetName = IntPtr.Zero;
            ActivityThreadCurrent = IntPtr.Zero;
            ActivityThreadCurrentApplication = IntPtr.Zero;
            ContextGetClassLoader = IntPtr.Zero;
            ClassLoaderLoadClass = IntPtr.Zero;
            ActivityThreadActivities = IntPtr.Zero;
            ArrayMapSize = IntPtr.Zero;
            ArrayMapValueAt = IntPtr.Zero;
            ActivityClientRecordActivity = IntPtr.Zero;
        }

        private static IntPtr RequireMethod(IntPtr method, string name)
        {
            ThrowIfJavaException("method " + name);
            if (method == IntPtr.Zero)
                throw new InvalidOperationException("Android method not found: " + name);
            return method;
        }
    }

    // Driver state lives here, not on an injected MonoBehaviour: ClassInjector would have to
    // project a component with IntPtr fields and helpers, which this IL2CPP build cannot do.
    private static IntPtr _windowHelperClass;
    private static IntPtr _windowApply;
    private static IntPtr _windowReset;
    private static IntPtr _activity;
    private static string _activitySource = string.Empty;
    private static float _nextApplyAt;
    private static int _screenWidth;
    private static int _screenHeight;
    private static int _activityMisses;
    private static bool _disabled;
    private static bool _queuedLogged;
    private static bool _running;
    private static GameObject? _letterbox;
    private static float _nextLetterboxCheck;
    private static bool _letterboxLogged;

    private const string LetterboxRootName = "LetterBoxOverlays";

    // Resolution/dispatch stay on UnityMain; Java posts window work to Android's UI thread.
    private static System.Collections.IEnumerator Driver()
    {
        if (_running)
            yield break;
        _running = true;
        var wasFocused = Application.isFocused;
        while (!_disabled)
        {
            var focused = Application.isFocused;
            var sizeChanged = Screen.width != _screenWidth || Screen.height != _screenHeight;
            if (sizeChanged)
            {
                _screenWidth = Screen.width;
                _screenHeight = Screen.height;
            }
            var regainedFocus = focused && !wasFocused;
            wasFocused = focused;
            RemoveLetterbox();
            if (Time.unscaledTime >= _nextApplyAt || regainedFocus || sizeChanged)
            {
                _nextApplyAt = Time.unscaledTime + ReapplyIntervalSeconds;
                Apply();
            }
            yield return null;
        }
        _running = false;
    }

    private static void Apply()
    {
        if (_disabled)
            return;
        try
        {
            if (!Api.TryInitialize(out var apiError))
            {
                _disabled = true;
                WarnOnce("api_unavailable", apiError);
                return;
            }
            var activity = EnsureActivity(out var activityChanged);
            if (activity == IntPtr.Zero)
            {
                if (++_activityMisses == ActivityMissesBeforeWarning)
                    WarnOnce("activity_unavailable", null);
                return;
            }
            _activityMisses = 0;
            AndroidBackBridge.EnsureInstalled(activity);
            if (activityChanged)
                _queuedLogged = false;
            EnsureWindowHelper();
            var queued = AndroidJNI.CallStaticBooleanMethod(
                _windowHelperClass,
                _windowApply,
                Api.ObjectArgs(activity)
            );
            ThrowIfJavaException("BppGameWindow.apply");
            if (queued)
                LogQueued();
        }
        catch (Exception exception)
        {
            ClearJavaException();
            ReleaseActivity();
            WarnOnce("apply_failed", exception);
        }
    }

    private static IntPtr EnsureActivity(out bool changed)
    {
        changed = false;
        if (IsActivityUsable(_activity))
            return _activity;
        ReleaseActivity();
        var resolved = ResolveActivity(out var source);
        if (resolved == IntPtr.Zero)
            return IntPtr.Zero;
        _activity = resolved;
        _activitySource = source;
        changed = true;
        return _activity;
    }

    private static void ReleaseActivity()
    {
        var activity = _activity;
        _activity = IntPtr.Zero;
        _activitySource = string.Empty;
        if (activity == IntPtr.Zero)
            return;
        try
        {
            AndroidJNI.DeleteGlobalRef(activity);
        }
        catch
        {
            // The reference is already unreachable; nothing else to release.
        }
    }

    private static void EnsureWindowHelper()
    {
        if (_windowHelperClass != IntPtr.Zero)
            return;
        var local = AndroidHostClassLoader.LoadLocal(WindowHelperName);
        IntPtr global = IntPtr.Zero;
        try
        {
            var apply = AndroidJNI.GetStaticMethodID(local, "apply", "(Landroid/app/Activity;)Z");
            ThrowIfJavaException("BppGameWindow.apply lookup");
            var reset = AndroidJNI.GetStaticMethodID(local, "reset", "()V");
            ThrowIfJavaException("BppGameWindow.reset lookup");
            if (apply == IntPtr.Zero || reset == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Android game-window helper methods are unavailable."
                );
            global = AndroidJNI.NewGlobalRef(local);
            ThrowIfJavaException("BppGameWindow global reference");
            if (global == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Could not retain the Android game-window helper."
                );
            _windowApply = apply;
            _windowReset = reset;
            _windowHelperClass = global;
            global = IntPtr.Zero;
        }
        finally
        {
            AndroidJNI.DeleteLocalRef(local);
            if (global != IntPtr.Zero)
                AndroidJNI.DeleteGlobalRef(global);
        }
    }

    private static void ResetWindowHelper()
    {
        try
        {
            if (_windowHelperClass != IntPtr.Zero && _windowReset != IntPtr.Zero)
                AndroidJNI.CallStaticVoidMethod(_windowHelperClass, _windowReset, Api.EmptyArgs);
            ThrowIfJavaException("BppGameWindow.reset");
        }
        catch (Exception exception)
        {
            ClearJavaException();
            WarnOnce("reset_failed", exception);
        }
        finally
        {
            DeleteGlobalQuietly(_windowHelperClass);
            _windowHelperClass = IntPtr.Zero;
            _windowApply = IntPtr.Zero;
            _windowReset = IntPtr.Zero;
        }
    }

    private static void LogQueued()
    {
        if (_queuedLogged)
            return;
        _queuedLogged = true;
        try
        {
            // Queue acceptance is not confirmation of bar visibility or surface geometry.
            BppLog.InfoEvent(
                new BppLogEvent(BppLogFeatureScope.Plugin, "plugin.android.fullscreen.queued"),
                ("activity_source", _activitySource)
            );
        }
        catch
        {
            // Logging must never throw into the game loop.
        }
    }

    // The game draws its own 16:9 bars through this root. Deactivating it lets the full-height
    // viewport show; if a scene recreates it, the next check removes it again. GameObject.Find is
    // throttled because it walks the scene.
    private static void RemoveLetterbox()
    {
        try
        {
            if (_letterbox == null && Time.unscaledTime >= _nextLetterboxCheck)
            {
                _nextLetterboxCheck = Time.unscaledTime + 1f;
                _letterbox = GameObject.Find(LetterboxRootName);
            }
            if (_letterbox == null || !_letterbox.activeSelf)
                return;
            _letterbox.SetActive(false);
            if (_letterboxLogged)
                return;
            _letterboxLogged = true;
            BppLog.InfoEvent(
                new BppLogEvent(
                    BppLogFeatureScope.Plugin,
                    "plugin.android.fullscreen.letterbox_removed"
                )
            );
        }
        catch
        {
            // A missing or renamed letterbox root must not break the immersive driver.
        }
    }
}
#endif
