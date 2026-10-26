package com.bepinex.android.bpp;

import android.app.Activity;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.util.Log;
import android.view.View;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import java.lang.ref.WeakReference;
import java.util.HashSet;
import java.util.Set;

/** Host-loaded window adapter, independent of the cloned game's Java classes and loader. */
public final class BppGameWindow {
    private static final String TAG = "BppGameWindow";
    private static final String LAUNCHER = "com.bepinex.android.bpp.BppLauncherActivity";
    private static final Handler MAIN = new Handler(Looper.getMainLooper());
    private static final int IMMERSIVE_FLAGS = View.SYSTEM_UI_FLAG_LAYOUT_STABLE
            | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
            | View.SYSTEM_UI_FLAG_FULLSCREEN | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
            | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY;

    // Reset cancels queued Activity captures and cannot race an in-flight UI-thread request.
    private static Object generation = new Object();
    private static WeakReference<Activity> loggedActivity = new WeakReference<>(null);
    private static final Set<String> loggedFailures = new HashSet<>();

    private BppGameWindow() {}

    /** True means queued, not that the requested fullscreen geometry has been observed. */
    public static synchronized boolean apply(final Activity activity) {
        if (activity == null) return false;
        final Object token = generation;
        return MAIN.postAtTime(new Runnable() {
            @Override public void run() {
                synchronized (BppGameWindow.class) {
                    if (generation != token) return;
                    try {
                        if (!isGameTarget(activity)) return;
                        Window window = activity.getWindow();
                        if (window == null) return;
                        View decor = window.getDecorView();
                        // The managed cadence retries after the decor attaches.
                        if (decor == null || !decor.isAttachedToWindow()) return;
                        applyWindow(window, decor);
                        if (loggedActivity.get() != activity) {
                            loggedActivity = new WeakReference<>(activity);
                            Log.i(TAG, "Window request executed on Android UI thread; activity="
                                    + activity.getClass().getName());
                        }
                    } catch (RuntimeException error) {
                        reportFailure("apply", error);
                    }
                }
            }
        }, token, SystemClock.uptimeMillis());
    }

    /** Releases queued work without mutating any window during teardown. */
    public static synchronized void reset() {
        MAIN.removeCallbacksAndMessages(generation);
        generation = new Object();
        loggedActivity.clear();
        loggedFailures.clear();
    }

    private static boolean isGameTarget(Activity activity) {
        try {
            if (activity == null || activity.isFinishing() || activity.isDestroyed()) return false;
            // Check runtime types: the game Activity may be cloned behind StubActivity.
            // Reject launcher subclasses too, without loading a launcher type in the game loader.
            for (Class<?> type = activity.getClass(); type != null; type = type.getSuperclass()) {
                String name = type.getName();
                if (name == null || name.length() == 0 || LAUNCHER.equals(name)) return false;
            }
            return true;
        } catch (RuntimeException error) {
            reportFailure("target_identity", error);
            return false;
        }
    }

    @SuppressWarnings("deprecation")
    private static void applyWindow(Window window, View decor) {
        // Cutout rejection must not prevent the independent immersive request.
        if (Build.VERSION.SDK_INT >= 28) {
            try {
                WindowManager.LayoutParams attributes = window.getAttributes();
                int mode = Build.VERSION.SDK_INT >= 30
                        ? WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS
                        : WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES;
                if (attributes.layoutInDisplayCutoutMode != mode) {
                    attributes.layoutInDisplayCutoutMode = mode;
                    window.setAttributes(attributes);
                }
            } catch (RuntimeException error) {
                reportFailure("cutout", error);
            }
        }
        window.addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
        decor.setFitsSystemWindows(false);
        View content = decor.findViewById(android.R.id.content);
        if (content != null) content.setFitsSystemWindows(false);
        decor.setSystemUiVisibility(IMMERSIVE_FLAGS);
        // Modern decor-fit follows legacy flags, which otherwise conflict with edge-to-edge.
        if (Build.VERSION.SDK_INT >= 30) Api30.apply(window, decor);
        // Redispatch real insets, never consume them or rewrite Unity child padding/margins.
        decor.requestApplyInsets();
        if (content != null) content.requestApplyInsets();
    }

    private static void reportFailure(String operation, RuntimeException error) {
        if (loggedFailures.add(operation)) Log.e(TAG, "Failed " + operation + "; will retry", error);
    }

    private static final class Api30 {
        static void apply(Window window, View decor) {
            window.setDecorFitsSystemWindows(false);
            WindowInsetsController controller = decor.getWindowInsetsController();
            if (controller != null) {
                controller.setSystemBarsBehavior(
                        WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
                controller.hide(WindowInsets.Type.systemBars());
            }
        }
    }
}
