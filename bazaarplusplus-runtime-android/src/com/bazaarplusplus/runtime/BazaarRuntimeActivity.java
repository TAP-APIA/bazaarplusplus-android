package com.bazaarplusplus.runtime;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.res.AssetManager;
import android.content.res.Resources;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.widget.TextView;
import dalvik.system.DexClassLoader;
import dalvik.system.DexFile;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.Enumeration;
import java.util.List;
import java.io.BufferedReader;
import java.io.FileReader;
import java.io.FileInputStream;
import java.nio.charset.StandardCharsets;

public final class BazaarRuntimeActivity extends Activity {
    private Object unityPlayer;
    private ClassLoader gameLoader;
    private GameRuntimeContext gameContext;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        RuntimeLog.reset(this);
        RuntimeLog.write(this, "activity onCreate package=" + getPackageName()
            + " pid=" + android.os.Process.myPid()
            + " cmdline=" + readCmdline());
        TextView loading = new TextView(this);
        loading.setText("正在准备纯游戏 Runtime Probe…");
        loading.setTextSize(18);
        loading.setPadding(32, 32, 32, 32);
        setContentView(loading);
        new Thread(() -> {
            try {
                GameVersion version = GameVersion.newest(this);
                RuntimeLog.write(this, "version apk=" + version.apk + " size=" + version.apk.length());
                RuntimePreparer.prepare(this, version);
                gameLoader = new DexClassLoader(version.apk.getAbsolutePath(), version.optimizedDir.getAbsolutePath(), version.nativeDir.getAbsolutePath(), getClassLoader());
                RuntimeLog.write(this, "DexClassLoader created nativePath=" + version.nativeDir);
                gameContext = new GameRuntimeContext(this, version, gameLoader);
                RuntimeLog.write(this, "game context created assets=" + gameContext.getAssets() + " package=" + gameContext.getPackageName());
                logOfficialEntrypoints(version);
                runOnUiThread(() -> constructUnity(gameContext));
            } catch (Throwable ex) {
                fail("prepare failed", ex);
            }
        }, "runtime-prepare").start();
    }

    private void constructUnity(Context gameContext) {
        try {
            Thread.currentThread().setContextClassLoader(gameLoader);
            Class<?> unityClass = Class.forName("com.unity3d.player.UnityPlayer", true, gameLoader);
            Class<?> concreteClass = findConcreteUnityPlayer(unityClass);
            RuntimeLog.write(this, "UnityPlayer class loaded=" + unityClass + " concrete=" + concreteClass + " loader=" + concreteClass.getClassLoader());
            try {
                Field currentActivity = unityClass.getDeclaredField("currentActivity");
                currentActivity.setAccessible(true);
                currentActivity.set(null, this);
                RuntimeLog.write(this, "UnityPlayer.currentActivity assigned");
            } catch (Throwable ex) {
                RuntimeLog.write(this, "currentActivity assignment skipped: " + ex);
            }
            Constructor<?> selected = null;
            for (Constructor<?> constructor : concreteClass.getDeclaredConstructors()) {
                RuntimeLog.write(this, "constructor candidate=" + constructor);
                Class<?>[] parameters = constructor.getParameterTypes();
                if (parameters.length > 0 && Context.class.isAssignableFrom(parameters[0])) { selected = constructor; break; }
            }
            if (selected == null) throw new Exception("未找到以 Context 开头的 UnityPlayer 构造函数");
            selected.setAccessible(true);
            Class<?>[] types = selected.getParameterTypes();
            Object[] args = new Object[types.length];
            // UnityPlayerForActivityOrService selects its mode using instanceof Activity.
            // This Activity delegates game-facing Context methods to the imported APK.
            args[0] = this;
            for (int i = 1; i < args.length; i++) args[i] = argumentValue(types[i]);
            RuntimeLog.write(this, "constructing UnityPlayer with " + types.length + " arguments");
            unityPlayer = selected.newInstance(args);
            RuntimeLog.write(this, "Unity controller constructed class=" + unityPlayer.getClass().getName());
            logUnityHostFields();
            logUnityState("constructed");
            View renderView = resolveRenderView(unityPlayer);
            setContentView(renderView);
            renderView.requestFocus();
            RuntimeLog.write(this, "Unity render view attached class=" + renderView.getClass().getName());
            invokeUnity("resume");
            invokeWindowFocus(hasWindowFocus());
            logUnityState("resumed");
            scheduleUnityState();
            scheduleSnapshots(renderView);
        } catch (Throwable ex) {
            fail("Unity construction failed", ex);
        }
    }

    private void logOfficialEntrypoints(GameVersion version) {
        PackageInfo info = getPackageManager().getPackageArchiveInfo(
            version.apk.getAbsolutePath(), PackageManager.GET_ACTIVITIES | PackageManager.GET_META_DATA);
        if (info == null) {
            RuntimeLog.write(this, "official package info unavailable");
            return;
        }
        ApplicationInfo app = info.applicationInfo;
        RuntimeLog.write(this, "official application=" + (app == null ? null : app.className)
            + " metadata=" + (app == null ? null : app.metaData));
        if (info.activities != null) {
            for (ActivityInfo activity : info.activities) {
                RuntimeLog.write(this, "official activity=" + activity.name
                    + " process=" + activity.processName
                    + " exported=" + activity.exported
                    + " metadata=" + activity.metaData);
            }
        }
    }

    private void logUnityHostFields() {
        for (Field field : allFields(unityPlayer.getClass())) {
            if (field.getType().isPrimitive() || Modifier.isStatic(field.getModifiers())) continue;
            try {
                field.setAccessible(true);
                Object value = field.get(unityPlayer);
                RuntimeLog.write(this, "unity host field " + field.getDeclaringClass().getSimpleName()
                    + "." + field.getName() + " type=" + field.getType().getName()
                    + " state=" + (value == null ? "NULL" : value.getClass().getName()));
            } catch (Throwable ex) {
                RuntimeLog.write(this, "unity host field " + field.getName() + " unreadable=" + ex.getClass().getSimpleName());
            }
        }
    }

    private void scheduleUnityState() {
        Handler handler = new Handler(Looper.getMainLooper());
        int[] delays = {250, 750, 1500};
        for (int delay : delays) handler.postDelayed(() -> logUnityState("delayed-" + delay + "ms"), delay);
    }

    private void logUnityState(String stage) {
        if (unityPlayer == null) return;
        StringBuilder result = new StringBuilder("unity state stage=").append(stage);
        String[] fields = {"mProcessKillRequested", "mQuitting", "m_IsNoWindowMode", "mContextType", "mState"};
        for (String name : fields) result.append(' ').append(name).append('=').append(readField(name));
        String[] methods = {"getProcessName", "getContextType", "getUaaLLaunchProcessType", "isUaaLUseCase", "isNativeInitialized", "getState"};
        for (String name : methods) result.append(' ').append(name).append('=').append(invokeNoArg(name));
        RuntimeLog.write(this, result.toString());
    }

    private Object invokeNoArg(String name) {
        try {
            Method method = findMethod(unityPlayer.getClass(), name);
            if (method == null) return "<missing>";
            method.setAccessible(true);
            return method.invoke(unityPlayer);
        } catch (Throwable ex) {
            return "<" + ex.getClass().getSimpleName() + ">";
        }
    }

    private Object readField(String name) {
        try {
            Field field = findField(unityPlayer.getClass(), name);
            if (field == null) return "<missing>";
            field.setAccessible(true);
            return field.get(unityPlayer);
        } catch (Throwable ex) {
            return "<" + ex.getClass().getSimpleName() + ">";
        }
    }

    private static Method findMethod(Class<?> type, String name) {
        for (Class<?> cursor = type; cursor != null; cursor = cursor.getSuperclass()) {
            try { return cursor.getDeclaredMethod(name); } catch (NoSuchMethodException ignored) {}
        }
        return null;
    }

    private static Field findField(Class<?> type, String name) {
        for (Class<?> cursor = type; cursor != null; cursor = cursor.getSuperclass()) {
            try { return cursor.getDeclaredField(name); } catch (NoSuchFieldException ignored) {}
        }
        return null;
    }

    private static String readCmdline() {
        try (FileInputStream input = new FileInputStream("/proc/self/cmdline")) {
            byte[] data = new byte[256];
            int count = input.read(data);
            if (count <= 0) return "<empty>";
            int end = 0; while (end < count && data[end] != 0) end++;
            return new String(data, 0, end, StandardCharsets.UTF_8);
        } catch (Exception ex) {
            return "<" + ex + ">";
        }
    }

    @Override public ClassLoader getClassLoader() { return gameLoader != null ? gameLoader : super.getClassLoader(); }
    @Override public AssetManager getAssets() { return gameContext != null ? gameContext.getAssets() : super.getAssets(); }
    @Override public Resources getResources() { return gameContext != null ? gameContext.getResources() : super.getResources(); }
    @Override public String getPackageName() { return gameContext != null ? gameContext.getPackageName() : super.getPackageName(); }
    @Override public ApplicationInfo getApplicationInfo() { return gameContext != null ? gameContext.getApplicationInfo() : super.getApplicationInfo(); }

    private void scheduleSnapshots(View renderView) {
        Handler handler = new Handler(Looper.getMainLooper());
        int[] delays = {1000, 5000, 15000};
        for (int delay : delays) handler.postDelayed(() -> logSnapshot(renderView), delay);
    }

    private void logSnapshot(View view) {
        RuntimeLog.write(this, "snapshot view=" + view.getClass().getName()
            + " size=" + view.getWidth() + "x" + view.getHeight()
            + " shown=" + view.isShown() + " attached=" + view.isAttachedToWindow()
            + " focus=" + view.hasWindowFocus()
            + " libunity=" + mappedLibrary("libunity.so")
            + " libil2cpp=" + mappedLibrary("libil2cpp.so")
            + " libmain=" + mappedLibrary("libmain.so"));
    }

    private static boolean mappedLibrary(String name) {
        try (BufferedReader reader = new BufferedReader(new FileReader("/proc/self/maps"))) {
            String line; while ((line = reader.readLine()) != null) if (line.contains(name)) return true;
        } catch (Exception ignored) {}
        return false;
    }

    private View resolveRenderView(Object controller) throws Exception {
        if (controller instanceof View) return (View)controller;
        Class<?> type = controller.getClass();
        View best = null;
        int bestScore = -1;
        for (Method method : allMethods(type)) {
            if (method.getParameterTypes().length != 0) continue;
            if (!View.class.isAssignableFrom(method.getReturnType())) continue;
            try {
                method.setAccessible(true);
                Object value = method.invoke(controller);
                RuntimeLog.write(this, "view method " + method.getName() + " returned=" + value);
                if (value instanceof View) {
                    int score = viewScore(method.getName(), (View)value);
                    if (score > bestScore) { best = (View)value; bestScore = score; }
                }
            } catch (Throwable ex) {
                RuntimeLog.write(this, "view method failed " + method.getName() + ": " + ex);
            }
        }
        for (Field field : allFields(type)) {
            if (!View.class.isAssignableFrom(field.getType())) continue;
            try {
                field.setAccessible(true);
                Object value = field.get(controller);
                RuntimeLog.write(this, "view field " + field.getName() + " value=" + value);
                if (value instanceof View) {
                    int score = viewScore(field.getName(), (View)value);
                    if (score > bestScore) { best = (View)value; bestScore = score; }
                }
            } catch (Throwable ex) {
                RuntimeLog.write(this, "view field failed " + field.getName() + ": " + ex);
            }
        }
        if (best != null) {
            RuntimeLog.write(this, "selected render view score=" + bestScore + " class=" + best.getClass().getName());
            return detach(best);
        }
        throw new Exception("Unity 控制器未暴露 View；方法和字段清单已写入日志");
    }

    private static int viewScore(String name, View view) {
        String lower = name.toLowerCase(java.util.Locale.ROOT);
        int score = view instanceof ViewGroup ? 100 : 10;
        if (lower.contains("frame") || lower.contains("root") || lower.contains("container")) score += 100;
        if (lower.contains("surface")) score -= 5;
        return score;
    }

    private static List<Method> allMethods(Class<?> type) {
        List<Method> result = new ArrayList<>();
        for (Class<?> cursor = type; cursor != null; cursor = cursor.getSuperclass()) {
            for (Method method : cursor.getDeclaredMethods()) result.add(method);
        }
        return result;
    }

    private static List<Field> allFields(Class<?> type) {
        List<Field> result = new ArrayList<>();
        for (Class<?> cursor = type; cursor != null; cursor = cursor.getSuperclass()) {
            for (Field field : cursor.getDeclaredFields()) result.add(field);
        }
        return result;
    }

    private static View detach(View view) {
        if (view.getParent() instanceof ViewGroup) ((ViewGroup)view.getParent()).removeView(view);
        return view;
    }

    private Class<?> findConcreteUnityPlayer(Class<?> baseClass) throws Exception {
        if (!Modifier.isAbstract(baseClass.getModifiers())) return baseClass;
        GameVersion version = GameVersion.newest(this);
        List<String> candidates = new ArrayList<>();
        DexFile dex = new DexFile(version.apk);
        try {
            Enumeration<String> entries = dex.entries();
            while (entries.hasMoreElements()) {
                String name = entries.nextElement();
                if (name.startsWith("com.unity3d.player.")) candidates.add(name);
            }
        } finally { dex.close(); }
        RuntimeLog.write(this, "Unity package class count=" + candidates.size());
        for (String name : candidates) {
            try {
                Class<?> candidate = Class.forName(name, false, gameLoader);
                if (candidate != baseClass && baseClass.isAssignableFrom(candidate) && !candidate.isInterface() && !Modifier.isAbstract(candidate.getModifiers())) {
                    RuntimeLog.write(this, "concrete UnityPlayer candidate=" + candidate.getName());
                    return candidate;
                }
            } catch (Throwable ex) {
                RuntimeLog.write(this, "class scan skipped " + name + ": " + ex.getClass().getSimpleName());
            }
        }
        throw new Exception("未找到 UnityPlayer 的具体子类；扫描了 " + candidates.size() + " 个 Unity 类");
    }

    private Object argumentValue(Class<?> type) {
        if (type.isInterface()) {
            RuntimeLog.write(this, "creating interface proxy for " + type.getName());
            return Proxy.newProxyInstance(gameLoader, new Class<?>[]{type}, (proxy, method, args) -> {
                RuntimeLog.write(this, "interface callback " + type.getSimpleName() + "." + method.getName());
                return defaultValue(method.getReturnType());
            });
        }
        return defaultValue(type);
    }

    private static Object defaultValue(Class<?> type) {
        if (!type.isPrimitive()) return null;
        if (type == boolean.class) return false;
        if (type == byte.class) return (byte)0;
        if (type == short.class) return (short)0;
        if (type == int.class) return 0;
        if (type == long.class) return 0L;
        if (type == float.class) return 0f;
        if (type == double.class) return 0d;
        if (type == char.class) return (char)0;
        return null;
    }

    private void invokeUnity(String name) {
        if (unityPlayer == null) return;
        try { Method method = unityPlayer.getClass().getMethod(name); method.invoke(unityPlayer); RuntimeLog.write(this, "UnityPlayer." + name + " invoked"); }
        catch (Throwable ex) { RuntimeLog.write(this, "UnityPlayer." + name + " failed: " + ex); }
    }

    @Override protected void onResume() { super.onResume(); invokeUnity("resume"); }
    @Override protected void onPause() { invokeUnity("pause"); super.onPause(); }
    @Override protected void onDestroy() { invokeUnity("destroy"); super.onDestroy(); }
    @Override public void onWindowFocusChanged(boolean hasFocus) { super.onWindowFocusChanged(hasFocus); invokeWindowFocus(hasFocus); }

    private void invokeWindowFocus(boolean hasFocus) {
        if (unityPlayer == null) return;
        try {
            Method method = unityPlayer.getClass().getMethod("windowFocusChanged", boolean.class);
            method.invoke(unityPlayer, hasFocus);
            RuntimeLog.write(this, "UnityPlayer.windowFocusChanged(" + hasFocus + ") invoked");
        } catch (Throwable ex) {
            RuntimeLog.write(this, "UnityPlayer.windowFocusChanged failed: " + ex);
        }
    }

    private void fail(String stage, Throwable ex) {
        RuntimeLog.write(this, stage + ": " + android.util.Log.getStackTraceString(ex));
        runOnUiThread(() -> {
            TextView error = new TextView(this);
            error.setText(stage + "\n\n" + ex + "\n\n日志：" + RuntimeLog.file(this));
            error.setTextIsSelectable(true); error.setPadding(32, 32, 32, 32); setContentView(error);
        });
    }
}
