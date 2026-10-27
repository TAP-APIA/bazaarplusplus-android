package com.bepinex.android.bpp;

import android.app.Activity;
import android.app.ActivityManager;
import android.app.Application;
import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.content.ContextWrapper;
import android.content.Intent;
import android.database.Cursor;
import android.graphics.Color;
import android.graphics.PixelFormat;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.TextView;
import java.io.File;
import java.io.FileOutputStream;
import java.lang.ref.WeakReference;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.List;
import java.util.Map;

/**
 * Heartbeat, foreground hold, wake lock and the first-run progress card for {@code :game}.
 */
public final class BppGameProcess extends ContentProvider {

    private static final String TAG = "BppGame";
    private static final String LAUNCHER_PACKAGE = "com.bepinex.android";
    private static final String DIAG_DIR = BppDiagnosticSessionLog.DIR_NAME;

    private static final long HEARTBEAT_MS = 3000L;
    private static final long STALL_MS = 20000L;
    private static final long WAKE_LOCK_MS = 30L * 60L * 1000L;
    private static final long POLL_INTERVAL_MS = 500L;
    private static final long POLL_TIMEOUT_MS = 15L * 60L * 1000L;
    private static final long SURFACE_STABLE_MS = 1500L;
    private static final long FGS_RETRY_MS = 30000L;
    private static final int INTEROP_BASELINE_CAP = 20000;

    private static volatile boolean pluginsReady;
    private static volatile boolean started;
    private static volatile boolean foregroundStarted;
    private static volatile boolean lifecycleRegistered;
    private static volatile boolean buildInfoLogged;
    private static volatile boolean pollScheduled;
    private static volatile boolean fgsNotNeededLogged;
    private static volatile long pollStartedAt;
    private static volatile long lastFgsAttemptAt;
    private static volatile int overlayAttached;
    private static volatile String overlayText = "";
    private static volatile String processName = "";
    private static volatile int interopBaseline;
    private static volatile int lastInteropDllCount;
    private static volatile Context originalContext;
    private static volatile Context appContext;

    private static final Handler MAIN = new Handler(Looper.getMainLooper());
    private static WeakReference<Activity> host = new WeakReference<Activity>(null);
    private static WeakReference<Activity> keepScreenHost = new WeakReference<Activity>(null);
    private static WeakReference<SurfaceView> firstFrameSurface = new WeakReference<SurfaceView>(null);
    private static long firstFrameValidSince;
    private static View overlayView;
    private static WindowManager overlayWm;
    private static boolean overlayInWindow;
    private static TextView bodyView;
    private static PowerManager.WakeLock generationLock;

    private static final Application.ActivityLifecycleCallbacks LIFECYCLE =
        new Application.ActivityLifecycleCallbacks() {
            @Override
            public void onActivityResumed(Activity activity) {
                logEvent(contextFor(activity), "lifecycle resumed activity="
                    + (activity == null ? "null" : activity.getClass().getName()));
                considerAttach(activity, "lifecycle");
            }

            @Override
            public void onActivityDestroyed(Activity activity) {
                if (host.get() == activity) {
                    logEvent(contextFor(activity), "lifecycle destroyed host activity="
                        + (activity == null ? "null" : activity.getClass().getName()));
                    detach("activity-destroyed");
                }
            }

            @Override public void onActivityCreated(Activity activity, Bundle savedInstanceState) {}
            @Override public void onActivityStarted(Activity activity) {}
            @Override public void onActivityPaused(Activity activity) {}
            @Override public void onActivityStopped(Activity activity) {}
            @Override public void onActivitySaveInstanceState(Activity activity, Bundle outState) {}
        };

    private static final Runnable POLL = new Runnable() {
        @Override
        public void run() {
            if (pluginsReady) {
                pollScheduled = false;
                return;
            }
            long now = System.currentTimeMillis();
            if (now - pollStartedAt > POLL_TIMEOUT_MS) {
                pollScheduled = false;
                logEvent(diagnosticsContext(null), "poll timeout after 15min host=" + hostName());
                return;
            }
            Activity activity = findResumedActivity(diagnosticsContext(null));
            if (activity != null) {
                logEvent(contextFor(activity), "poll tick found activity="
                    + activity.getClass().getName() + " host=" + hostName());
                if (isBppLauncherActivity(activity)) {
                    logEvent(contextFor(activity), "poll attach skipped BppLauncherActivity");
                } else {
                    attach(activity, "poll");
                    checkFirstFrame(activity);
                }
            }
            if (!pluginsReady) {
                MAIN.postDelayed(this, POLL_INTERVAL_MS);
            } else {
                pollScheduled = false;
            }
        }
    };

    @Override
    public boolean onCreate() {
        Context context = getContext();
        if (context != null) {
            try {
                start(context, "BppGameProcess.ContentProvider.onCreate");
            } catch (Throwable t) {
                logEvent(context, "BppGameProcess.ContentProvider.onCreate start failed", t);
            }
        } else {
            Log.w(TAG, "BppGameProcess.ContentProvider.onCreate context=null");
        }
        return true;
    }

    public static void start(Context context) {
        start(context, sourceFromStack());
    }

    /** Starts the overlay and heartbeat only when the caller is the real {@code :game} process. */
    public static void start(Context context, String source) {
        try {
            if (context == null) {
                Log.w(TAG, "start called by " + source + " ignored reason=null context");
                return;
            }
            Context captured = captureOriginalContext(context);
            Context diag = captured != null ? captured : context;
            String process = currentProcessName(diag);
            int pid = android.os.Process.myPid();
            logBuildInfoOnce(diag);
            logEvent(diag, "start called by " + source
                + " context=" + context.getClass().getName()
                + " contextPkg=" + safePackage(context)
                + " originalPkg=" + safePackage(captured)
                + " process=" + process
                + " pid=" + pid);
            if (!isGameProcess(process)) {
                logEvent(diag, "skipped process=" + process + " pid=" + pid + " source=" + source);
                return;
            }
            tryRegisterLifecycle(context, source);
            if (!markStarted()) {
                logEvent(diag, "start already active process=" + process
                    + " pid=" + pid + " source=" + source);
                return;
            }
            appContext = captured != null ? captured : context;
            processName = process;
            noteHook(appContext, "game-hook-started", processName);
            try {
                BppDiagnosticSessionLog.ensureCapture(stableContext(appContext), "game", pid);
            } catch (Throwable t) {
                logEvent(appContext, "ensureCapture failed process=" + process, t);
            }
            holdWakeLock(appContext);
            startPoll(pid);
            final Context heartbeatContext = appContext;
            final long startedAt = System.currentTimeMillis();
            Thread thread = new Thread(new Runnable() {
                @Override
                public void run() {
                    heartbeat(heartbeatContext, startedAt, android.os.Process.myPid());
                }
            }, "bpp-game-heartbeat");
            thread.setDaemon(true);
            thread.start();
            logEvent(appContext, "heartbeat thread started pid=" + pid);
        } catch (Throwable t) {
            if (context != null) {
                logEvent(context, "start failed source=" + source, t);
            } else {
                Log.w(TAG, "start failed source=" + source, t);
            }
        }
    }

    public static void markPluginsReady() {
        finishGeneration("markPluginsReady");
    }

    static void logEvent(Context context, String message) {
        logEvent(context, message, null);
    }

    static void logEvent(Context context, String message, Throwable t) {
        String lineMessage = message;
        if (t != null) {
            Log.w(TAG, message, t);
            lineMessage = message + " error=" + t.getClass().getName() + ": " + t.getMessage();
        } else {
            Log.i(TAG, message);
        }
        Context diag = originalContext;
        if (diag == null) diag = appContext;
        if (diag == null) diag = context;
        if (diag == null) return;
        noteHook(diag, lineMessage, processName);
    }

    private static boolean markStarted() {
        synchronized (BppGameProcess.class) {
            if (started) return false;
            started = true;
            return true;
        }
    }

    private static void finishGeneration(String reason) {
        synchronized (BppGameProcess.class) {
            if (pluginsReady) return;
            pluginsReady = true;
        }
        Context context = diagnosticsContext(null);
        logEvent(context, "generation finish reason=" + reason
            + " process=" + processName + " pid=" + android.os.Process.myPid());
        MAIN.removeCallbacks(POLL);
        pollScheduled = false;
        MAIN.post(new Runnable() {
            @Override
            public void run() {
                detach("remove: " + reason);
                stopForegroundHold(reason);
                releaseWakeLock(reason);
                logEvent(diagnosticsContext(null), "removal complete reason=" + reason);
            }
        });
        writeReadyState(reason);
    }

    private static void writeReadyState(String reason) {
        Context context = diagnosticsContext(null);
        if (context == null) return;
        try {
            File state = new File(diagnosticsDir(context), "generation-state.txt");
            writeAtomically(
                state,
                "stage=ready\nlabel="
                    + BppDiagnosticBundle.generationStageLabel("ready")
                    + "\nreason=" + reason + "\n"
            );
        } catch (Throwable t) {
            Log.w(TAG, "write ready state failed reason=" + reason, t);
        }
    }

    private static void startPoll(int pid) {
        synchronized (BppGameProcess.class) {
            if (pollScheduled) return;
            pollScheduled = true;
            pollStartedAt = System.currentTimeMillis();
        }
        logEvent(diagnosticsContext(null), "poll started pid=" + pid
            + " intervalMs=" + POLL_INTERVAL_MS + " timeoutMs=" + POLL_TIMEOUT_MS);
        MAIN.postDelayed(POLL, 0L);
    }

    private static void tryRegisterLifecycle(Context context, String source) {
        if (lifecycleRegistered) return;
        Application application = findApplication(context);
        if (application == null) {
            logEvent(diagnosticsContext(context), "lifecycle register deferred reason=no Application"
                + " context=" + context.getClass().getName() + " source=" + source);
            return;
        }
        try {
            synchronized (BppGameProcess.class) {
                if (lifecycleRegistered) return;
                application.registerActivityLifecycleCallbacks(LIFECYCLE);
                lifecycleRegistered = true;
            }
            logEvent(diagnosticsContext(context), "lifecycle registered application="
                + application.getClass().getName() + " source=" + source);
        } catch (Throwable t) {
            logEvent(diagnosticsContext(context), "lifecycle register failed source=" + source, t);
        }
    }

    private static Application findApplication(Context context) {
        if (context == null) return null;
        if (context instanceof Application) return (Application) context;
        try {
            Context app = context.getApplicationContext();
            if (app instanceof Application) return (Application) app;
        } catch (Throwable t) {
            Log.w(TAG, "findApplication getApplicationContext failed", t);
        }
        Context original = originalContext;
        if (original != null && original != context) {
            if (original instanceof Application) return (Application) original;
            try {
                Context app = original.getApplicationContext();
                if (app instanceof Application) return (Application) app;
            } catch (Throwable t) {
                Log.w(TAG, "findApplication original getApplicationContext failed", t);
            }
        }
        return null;
    }

    private static void considerAttach(final Activity activity, final String method) {
        if (activity == null || pluginsReady) return;
        if (isBppLauncherActivity(activity)) {
            logEvent(contextFor(activity), "attach skipped BppLauncherActivity method=" + method);
            return;
        }
        MAIN.post(new Runnable() {
            @Override
            public void run() {
                attach(activity, method);
            }
        });
    }

    private static void attach(Activity activity, String method) {
        if (pluginsReady) return;
        if (activity == null) {
            logEvent(diagnosticsContext(null), "attach failed reason=null activity method=" + method);
            return;
        }
        if (isBppLauncherActivity(activity)) {
            logEvent(contextFor(activity), "attach skipped BppLauncherActivity method=" + method);
            return;
        }
        if (activity.isFinishing() || activity.isDestroyed()) {
            logEvent(contextFor(activity), "attach failed reason=activity finishing/destroyed"
                + " activity=" + activity.getClass().getName() + " method=" + method);
            return;
        }
        try {
            View decor = activity.getWindow().getDecorView();
            sinkSurfaces(decor);
            Activity current = host.get();
            if (overlayView != null
                && current == activity
                && overlayAttached == 1
                && overlayView.isAttachedToWindow()) {
                refreshOverlay();
                ensureForeground(activity, method);
                return;
            }
            if (current != null && current != activity) {
                logEvent(contextFor(activity), "reattach host changed from="
                    + current.getClass().getName() + " to=" + activity.getClass().getName()
                    + " method=" + method);
            }
            detachView("reattach-or-refresh");
            activity.getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
            keepScreenHost = new WeakReference<Activity>(activity);
            FrameLayout root = buildOverlay(activity);
            IBinder token = decor.getWindowToken();
            boolean added = false;
            String used = null;
            if (token != null) {
                added = addPanel(activity, root, token);
                if (added) {
                    used = "TYPE_APPLICATION_PANEL";
                } else {
                    logEvent(contextFor(activity), "attach panel failed, fallback to content"
                        + " activity=" + activity.getClass().getName() + " method=" + method);
                }
            } else {
                logEvent(contextFor(activity), "attach panel skipped reason=null window token"
                    + " activity=" + activity.getClass().getName() + " method=" + method);
            }
            if (!added) {
                added = addToContent(activity, root);
                if (added) {
                    used = "content";
                } else {
                    logEvent(contextFor(activity), "attach failed reason=no window/content parent"
                        + " activity=" + activity.getClass().getName() + " method=" + method);
                }
            }
            if (!added) {
                clearKeepScreenOn("attach-failed");
                return;
            }
            overlayView = root;
            host = new WeakReference<Activity>(activity);
            overlayAttached = 1;
            refreshOverlay();
            ensureForeground(activity, method);
            logEvent(contextFor(activity), "attach success activity="
                + activity.getClass().getName() + " method=" + method + " window=" + used);
        } catch (Throwable t) {
            logEvent(contextFor(activity), "attach failed by exception activity="
                + activity.getClass().getName() + " method=" + method, t);
            clearKeepScreenOn("attach-exception");
        }
    }

    /** Unity's SurfaceView is composited in its own window and can cover a normal dialog. */
    private static void sinkSurfaces(View view) {
        if (view instanceof SurfaceView) {
            SurfaceView surface = (SurfaceView) view;
            try {
                surface.setZOrderOnTop(false);
            } catch (Throwable t) {
                Log.w(TAG, "sinkSurfaces setZOrderOnTop failed", t);
            }
            try {
                surface.setZOrderMediaOverlay(false);
            } catch (Throwable t) {
                Log.w(TAG, "sinkSurfaces setZOrderMediaOverlay failed", t);
            }
        }
        if (view instanceof ViewGroup) {
            ViewGroup group = (ViewGroup) view;
            for (int i = 0; i < group.getChildCount(); i++) sinkSurfaces(group.getChildAt(i));
        }
    }

    private static boolean addPanel(Activity activity, View root, IBinder token) {
        try {
            WindowManager manager = activity.getWindowManager();
            WindowManager.LayoutParams params = new WindowManager.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT,
                WindowManager.LayoutParams.TYPE_APPLICATION_PANEL,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                    | WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL
                    | WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN
                    | WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON
                    | WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED
                    | WindowManager.LayoutParams.FLAG_FULLSCREEN,
                PixelFormat.TRANSLUCENT
            );
            params.token = token;
            params.gravity = Gravity.TOP | Gravity.START;
            if (Build.VERSION.SDK_INT >= 30) {
                params.layoutInDisplayCutoutMode =
                    WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS;
            }
            manager.addView(root, params);
            overlayWm = manager;
            overlayInWindow = true;
            return true;
        } catch (Throwable t) {
            Log.w(TAG, "addPanel failed activity=" + activity.getClass().getName(), t);
            overlayWm = null;
            overlayInWindow = false;
            return false;
        }
    }

    private static boolean addToContent(Activity activity, View root) {
        try {
            View content = activity.findViewById(android.R.id.content);
            ViewGroup parent;
            if (content instanceof ViewGroup) parent = (ViewGroup) content;
            else if (activity.getWindow().getDecorView() instanceof ViewGroup) {
                parent = (ViewGroup) activity.getWindow().getDecorView();
            } else return false;
            root.setElevation(10000f);
            parent.addView(root, new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT
            ));
            root.bringToFront();
            overlayWm = null;
            overlayInWindow = false;
            return true;
        } catch (Throwable t) {
            Log.w(TAG, "addToContent failed activity=" + activity.getClass().getName(), t);
            return false;
        }
    }

    private static void ensureForeground(Activity activity, String method) {
        if (foregroundStarted || pluginsReady) return;
        long now = System.currentTimeMillis();
        if (now - lastFgsAttemptAt < FGS_RETRY_MS) return;
        lastFgsAttemptAt = now;
        Context starter = originalContext;
        if (starter == null) starter = activity == null ? appContext : activity.getApplicationContext();
        if (starter == null && activity != null) starter = activity;
        if (starter == null) {
            logEvent(appContext, "FGS start skipped reason=no context method=" + method);
            return;
        }
        if (!needsForegroundHold(starter)) {
            if (!fgsNotNeededLogged) {
                fgsNotNeededLogged = true;
                logEvent(starter, "FGS not needed reason=interop dlls exist method=" + method);
            }
            return;
        }
        try {
            Intent intent = new Intent(starter, BppGenerationForegroundService.class);
            starter.startForegroundService(intent);
            foregroundStarted = true;
            logEvent(starter, "FGS start ok method=" + method
                + " contextPkg=" + safePackage(starter));
        } catch (Throwable t) {
            logEvent(starter, "FGS start exception method=" + method
                + " contextPkg=" + safePackage(starter), t);
        }
    }

    private static boolean needsForegroundHold(Context context) {
        try {
            File interop = new File(gameRoot(context), "BepInEx/interop");
            File[] files = interop.listFiles();
            if (files == null) return true;
            int dlls = 0;
            for (int i = 0; i < files.length; i++) {
                String name = files[i].getName().toLowerCase(java.util.Locale.ROOT);
                if (name.endsWith(".dll")) dlls++;
            }
            return dlls <= 0;
        } catch (Throwable t) {
            logEvent(context, "needsForegroundHold failed", t);
            return true;
        }
    }

    private static void stopForegroundHold(String reason) {
        Context context = originalContext;
        if (context == null) context = diagnosticsContext(null);
        if (context == null) {
            logEvent(appContext, "FGS stop skipped reason=no context removalReason=" + reason);
            return;
        }
        try {
            boolean stopped = context.stopService(new Intent(context, BppGenerationForegroundService.class));
            logEvent(context, "FGS stop requested reason=" + reason
                + " started=" + (foregroundStarted ? 1 : 0) + " stopped=" + stopped);
        } catch (Throwable t) {
            logEvent(context, "FGS stop exception reason=" + reason, t);
        }
        foregroundStarted = false;
    }

    private static void holdWakeLock(Context context) {
        Context target = context != null ? context : diagnosticsContext(null);
        if (target == null) {
            logEvent(appContext, "wake lock acquire skipped reason=no context");
            return;
        }
        try {
            if (generationLock != null && generationLock.isHeld()) {
                logEvent(target, "wake lock already held tag=bpp:generation");
                return;
            }
            PowerManager manager = (PowerManager) target.getSystemService(Context.POWER_SERVICE);
            if (manager == null) {
                logEvent(target, "wake lock acquire failed reason=no PowerManager");
                return;
            }
            PowerManager.WakeLock lock = manager.newWakeLock(
                PowerManager.PARTIAL_WAKE_LOCK,
                "bpp:generation"
            );
            lock.setReferenceCounted(false);
            lock.acquire(WAKE_LOCK_MS);
            generationLock = lock;
            logEvent(target, "wake lock acquired tag=bpp:generation timeoutMs=" + WAKE_LOCK_MS);
        } catch (Throwable t) {
            logEvent(target, "wake lock acquire failed tag=bpp:generation", t);
        }
    }

    private static void releaseWakeLock(String reason) {
        Context context = diagnosticsContext(null);
        PowerManager.WakeLock lock = generationLock;
        generationLock = null;
        if (lock == null) {
            logEvent(context, "wake lock release skipped reason=null removalReason=" + reason);
            return;
        }
        try {
            if (lock.isHeld()) lock.release();
            logEvent(context, "wake lock released reason=" + reason + " tag=bpp:generation");
        } catch (Throwable t) {
            logEvent(context, "wake lock release failed reason=" + reason, t);
        }
    }

    private static FrameLayout buildOverlay(Activity activity) {
        FrameLayout root = new FrameLayout(activity);
        root.setClickable(true);
        root.setBackgroundColor(Color.parseColor("#F0121418"));
        FrameLayout.LayoutParams cardParams = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT,
            Gravity.CENTER
        );
        int side = dp(activity, 28);
        cardParams.leftMargin = side;
        cardParams.rightMargin = side;
        root.addView(buildCard(activity), cardParams);
        return root;
    }

    private static LinearLayout buildCard(Activity activity) {
        LinearLayout card = new LinearLayout(activity);
        card.setOrientation(LinearLayout.VERTICAL);
        int pad = dp(activity, 20);
        card.setPadding(pad, pad, pad, pad);
        GradientDrawable background = new GradientDrawable();
        background.setColor(Color.parseColor("#F0141418"));
        background.setCornerRadius(dp(activity, 16));
        background.setStroke(dp(activity, 1), Color.parseColor("#E6C576"));
        card.setBackground(background);
        TextView title = new TextView(activity);
        title.setTextColor(Color.parseColor("#F5E6C8"));
        title.setTextSize(TypedValue.COMPLEX_UNIT_SP, 18f);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        title.setText("模组准备中");
        title.setGravity(Gravity.CENTER_HORIZONTAL);
        card.addView(title);
        bodyView = new TextView(activity);
        bodyView.setGravity(Gravity.CENTER_HORIZONTAL);
        bodyView.setTextColor(Color.parseColor("#F5E6C8"));
        bodyView.setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f);
        if (overlayText == null || overlayText.length() == 0) {
            overlayText = "阶段：准备中\n已用时：00:00";
        }
        bodyView.setText(overlayText);
        LinearLayout.LayoutParams bodyParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        );
        bodyParams.topMargin = dp(activity, 12);
        card.addView(bodyView, bodyParams);
        ProgressBar bar = new ProgressBar(activity, null, android.R.attr.progressBarStyleHorizontal);
        bar.setIndeterminate(true);
        LinearLayout.LayoutParams barParams = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            dp(activity, 8)
        );
        barParams.topMargin = dp(activity, 14);
        card.addView(bar, barParams);
        return card;
    }

    private static void refreshOverlay() {
        TextView view = bodyView;
        if (view == null) return;
        view.setText(overlayText == null ? "" : overlayText);
    }

    private static void detach(String reason) {
        detachView(reason);
        host = new WeakReference<Activity>(null);
    }

    private static void detachView(String reason) {
        View view = overlayView;
        WindowManager manager = overlayWm;
        boolean inWindow = overlayInWindow;
        overlayView = null;
        overlayWm = null;
        overlayInWindow = false;
        overlayAttached = 0;
        bodyView = null;
        if (view != null) {
            try {
                if (inWindow && manager != null) manager.removeViewImmediate(view);
                else if (view.getParent() instanceof ViewGroup) {
                    ((ViewGroup) view.getParent()).removeView(view);
                }
                logEvent(diagnosticsContext(null), "overlay removed reason=" + reason);
            } catch (Throwable t) {
                logEvent(diagnosticsContext(null), "overlay remove failed reason=" + reason, t);
            }
        }
        clearKeepScreenOn(reason);
    }

    private static void clearKeepScreenOn(String reason) {
        Activity activity = keepScreenHost.get();
        keepScreenHost = new WeakReference<Activity>(null);
        if (activity == null) return;
        try {
            activity.getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
            logEvent(contextFor(activity), "keep-screen-on cleared reason=" + reason
                + " activity=" + activity.getClass().getName());
        } catch (Throwable t) {
            logEvent(contextFor(activity), "clear keep-screen-on failed reason=" + reason, t);
        }
    }

    private static void checkFirstFrame(Activity activity) {
        if (pluginsReady || activity == null || lastInteropDllCount <= 0) return;
        try {
            SurfaceView candidate = findValidSurface(activity.getWindow().getDecorView());
            long now = System.currentTimeMillis();
            SurfaceView previous = firstFrameSurface.get();
            if (candidate == null) {
                if (previous != null) {
                    firstFrameSurface = new WeakReference<SurfaceView>(null);
                    firstFrameValidSince = 0L;
                }
                return;
            }
            if (candidate != previous) {
                firstFrameSurface = new WeakReference<SurfaceView>(candidate);
                firstFrameValidSince = now;
                logEvent(contextFor(activity), "first-frame candidate surface="
                    + candidate.getClass().getName()
                    + " size=" + candidate.getWidth() + "x" + candidate.getHeight()
                    + " interopDlls=" + lastInteropDllCount);
                return;
            }
            if (firstFrameValidSince == 0L) firstFrameValidSince = now;
            if (now - firstFrameValidSince >= SURFACE_STABLE_MS) {
                logEvent(contextFor(activity), "unity first frame detected size="
                    + candidate.getWidth() + "x" + candidate.getHeight()
                    + " stableMs=" + (now - firstFrameValidSince)
                    + " interopDlls=" + lastInteropDllCount);
                finishGeneration("unity-first-frame");
            }
        } catch (Throwable t) {
            logEvent(contextFor(activity), "first-frame check failed", t);
        }
    }

    private static SurfaceView findValidSurface(View view) {
        if (view == null) return null;
        if (view instanceof SurfaceView) {
            SurfaceView surfaceView = (SurfaceView) view;
            try {
                if (!surfaceView.isShown()) return null;
                if (surfaceView.getWidth() <= 0 || surfaceView.getHeight() <= 0) return null;
                SurfaceHolder holder = surfaceView.getHolder();
                if (holder == null) return null;
                Surface surface = holder.getSurface();
                if (surface != null && surface.isValid()) return surfaceView;
            } catch (Throwable t) {
                Log.w(TAG, "findValidSurface failed view="
                    + surfaceView.getClass().getName(), t);
            }
            return null;
        }
        if (view instanceof ViewGroup) {
            ViewGroup group = (ViewGroup) view;
            for (int i = 0; i < group.getChildCount(); i++) {
                SurfaceView found = findValidSurface(group.getChildAt(i));
                if (found != null) return found;
            }
        }
        return null;
    }

    private static Activity findResumedActivity(Context diag) {
        try {
            Class<?> clazz = Class.forName("android.app.ActivityThread");
            Method current = clazz.getDeclaredMethod("currentActivityThread");
            current.setAccessible(true);
            Object thread = current.invoke(null);
            if (thread == null) return null;
            Field field = clazz.getDeclaredField("mActivities");
            field.setAccessible(true);
            Object value = field.get(thread);
            if (!(value instanceof Map)) {
                logEvent(diag, "poll mActivities unexpected type="
                    + (value == null ? "null" : value.getClass().getName()));
                return null;
            }
            Map<?, ?> activities = (Map<?, ?>) value;
            for (Object record : activities.values()) {
                if (record == null) continue;
                Field activityField = findField(record.getClass(), "activity");
                if (activityField == null) {
                    logEvent(diag, "poll record missing activity field class="
                        + record.getClass().getName());
                    continue;
                }
                Object activityObject = activityField.get(record);
                if (!(activityObject instanceof Activity)) continue;
                Activity activity = (Activity) activityObject;
                if (activity.isFinishing() || activity.isDestroyed()) continue;
                Field pausedField = findField(record.getClass(), "paused");
                if (pausedField == null) {
                    logEvent(diag, "poll record missing paused field class="
                        + record.getClass().getName());
                    continue;
                }
                Object pausedObject = pausedField.get(record);
                boolean paused = pausedObject instanceof Boolean
                    && ((Boolean) pausedObject).booleanValue();
                if (!paused) return activity;
            }
        } catch (Throwable t) {
            logEvent(diag, "poll findResumedActivity failed", t);
        }
        return null;
    }

    private static Field findField(Class<?> type, String name) {
        Class<?> current = type;
        while (current != null) {
            try {
                Field field = current.getDeclaredField(name);
                field.setAccessible(true);
                return field;
            } catch (NoSuchFieldException ex) {
                current = current.getSuperclass();
            }
        }
        return null;
    }

    private static void heartbeat(Context context, long startedAt, int pid) {
        File gameRoot = gameRoot(context);
        int interopAtStart = BppDiagnosticBundle.summarizeTree(
            new File(gameRoot, "BepInEx/interop"), ".dll", 2).files;
        File logOutput = new File(gameRoot, "BepInEx/LogOutput.log");
        long logAtStart = logOutput.isFile() ? logOutput.length() : -1L;
        long logMtimeAtStart = logOutput.isFile() ? logOutput.lastModified() : 0L;
        int baseline = readBaseline(context);
        interopBaseline = baseline;
        String lastStage = "";
        long lastAdvanceAt = startedAt;
        long lastCpu = -1L;
        long lastCpuAt = startedAt;
        boolean dumpedStall = false;
        logEvent(context, "heartbeat loop started pid=" + pid);
        while (!pluginsReady) {
            try {
                long now = System.currentTimeMillis();
                long elapsed = Math.max(0L, (now - startedAt) / 1000L);
                BppDiagnosticBundle.GenerationProbe probe = new BppDiagnosticBundle.GenerationProbe();
                probe.interopFilesAtStart = interopAtStart;
                File unityLibs = new File(gameRoot, "BepInEx/unity-libs");
                BppDiagnosticBundle.DirStat unity = BppDiagnosticBundle.summarizeTree(
                    unityLibs, null, 1);
                probe.unityLibFiles = unity.files;
                File[] zips = unityLibs.listFiles();
                if (zips != null) {
                    for (int i = 0; i < zips.length; i++) {
                        String name = zips[i].getName().toLowerCase(java.util.Locale.ROOT);
                        if (name.endsWith(".zip")) probe.unityZipBytes += zips[i].length();
                    }
                }
                BppDiagnosticBundle.DirStat interop = BppDiagnosticBundle.summarizeTree(
                    new File(gameRoot, "BepInEx/interop"), ".dll", 2);
                probe.interopFiles = interop.files;
                probe.interopBytes = interop.bytes;
                probe.dummyFiles = BppDiagnosticBundle.summarizeTree(
                    new File(gameRoot, "BepInEx/dummy"), null, 2).files;
                boolean logNew = logOutput.isFile()
                    && (logOutput.lastModified() > logMtimeAtStart
                    || logOutput.length() != logAtStart);
                probe.logIsNew = logNew;
                probe.logOutputBytes = logNew ? logOutput.length() : 0L;
                String stage = BppDiagnosticBundle.generationStageId(probe);
                String label = BppDiagnosticBundle.generationStageLabel(stage);
                lastInteropDllCount = probe.interopFiles;
                if (!stage.equals(lastStage)) {
                    logEvent(context, "stage change from=" + lastStage + " to=" + stage
                        + " label=" + label + " elapsedSec=" + elapsed);
                    lastStage = stage;
                    lastAdvanceAt = now;
                    dumpedStall = false;
                }
                long cpu = cpuTicks();
                if (lastCpu >= 0 && cpu > lastCpu + 1) {
                    lastCpuAt = now;
                    dumpedStall = false;
                }
                lastCpu = cpu;
                int importance = importance(context, pid);
                long rss = vmRssKb();
                int percent = BppDiagnosticBundle.generationPercent(probe, baseline);
                String line = BppDiagnosticBundle.nowIso()
                    + " event=heartbeat"
                    + " stage=" + stage
                    + " elapsedSec=" + elapsed
                    + " interopFiles=" + probe.interopFiles
                    + " interopBytes=" + probe.interopBytes
                    + " unityLibFiles=" + probe.unityLibFiles
                    + " unityZipBytes=" + probe.unityZipBytes
                    + " dummyFiles=" + probe.dummyFiles
                    + " logOutputBytes=" + probe.logOutputBytes
                    + " rssKb=" + rss
                    + " cpuTicks=" + cpu
                    + " importance=" + importance
                    + " importanceName=" + BppDiagnosticBundle.importanceName(importance)
                    + " pid=" + pid
                    + " process=" + processName
                    + " overlay=" + overlayAttached
                    + " foreground=" + (foregroundStarted ? 1 : 0)
                    + " percent=" + percent;
                publish(context, gameRoot, probe, stage, label, elapsed, percent, rss, importance, line);
                patchConfigs(context, gameRoot);
                boolean stalled = now - lastAdvanceAt >= STALL_MS && now - lastCpuAt >= STALL_MS;
                if (stalled && !dumpedStall && !"ready".equals(stage)) {
                    dumpedStall = true;
                    dumpStacks(context, stage, elapsed);
                }
                if (logNew && logMentionsPlugins(logOutput)) {
                    logEvent(context, "generation finish detected LogOutput.log"
                        + " bytes=" + logOutput.length() + " stage=" + stage);
                    finishGeneration("LogOutput.log chainloader");
                    break;
                }
                if (generationFinishedByStage(stage, probe)) {
                    logEvent(context, "generation finish detected stage=" + stage
                        + " interopFiles=" + probe.interopFiles);
                    finishGeneration("stage=" + stage);
                    break;
                }
                if (probe.interopFiles > baseline) {
                    baseline = probe.interopFiles;
                    interopBaseline = baseline;
                    writeBaseline(context, baseline);
                }
            } catch (Throwable t) {
                logEvent(context, "heartbeat tick failed", t);
            }
            try {
                Thread.sleep(HEARTBEAT_MS);
            } catch (InterruptedException interrupted) {
                Thread.currentThread().interrupt();
                logEvent(context, "heartbeat interrupted pid=" + pid);
                return;
            }
        }
        logEvent(context, "heartbeat loop ended pid=" + pid);
    }

    private static boolean generationFinishedByStage(
        String stage,
        BppDiagnosticBundle.GenerationProbe probe
    ) {
        if ("ready".equals(stage) || "complete".equals(stage) || "finished".equals(stage)) {
            return true;
        }
        return "plugins".equals(stage) && probe.interopFiles > 0;
    }

    private static void publish(
        Context context,
        File gameRoot,
        BppDiagnosticBundle.GenerationProbe probe,
        String stage,
        String label,
        long elapsed,
        int percent,
        long rss,
        int importance,
        String line
    ) {
        boolean firstRun = probe.interopFilesAtStart <= 0 && probe.interopFiles == 0
            || (probe.interopFilesAtStart <= 0
            && !"plugins".equals(stage)
            && !"ready".equals(stage));
        StringBuilder card = new StringBuilder();
        if (probe.interopFilesAtStart <= 0 && probe.interopFiles == 0) {
            card.append(BppDiagnosticBundle.FIRST_RUN_OVERLAY_TEXT).append("\n\n");
        } else {
            card.append("正在加载模组，请保持在前台，不要切走\n\n");
        }
        card.append("阶段：").append(label).append('\n');
        card.append("已用时：").append(formatElapsed(elapsed)).append('\n');
        if (percent >= 0) card.append("进度：").append(percent).append("%\n");
        else card.append("进度：这一步暂时数不出百分比\n");
        card.append("Unity 基础库：").append(probe.unityLibFiles).append(" 个文件");
        if (probe.unityZipBytes > 0) {
            card.append("，压缩包 ").append(megabytes(probe.unityZipBytes));
        }
        card.append('\n');
        card.append("interop：").append(probe.interopFiles).append(" 个文件，")
            .append(megabytes(probe.interopBytes)).append('\n');
        if (probe.dummyFiles > 0) {
            card.append("Cpp2IL 临时文件：").append(probe.dummyFiles).append(" 个\n");
        }
        card.append("内存 RSS：").append(rss < 0 ? "未知" : megabytes(rss * 1024L)).append('\n');
        if ("cpp2il".equals(stage)) {
            card.append("\n分析游戏代码时几分钟内不会写出新文件，灰色画面是正常的。");
        }
        if (importance >= 400) {
            card.append("\n游戏已经不在前台，系统可能因内存不足把它关掉。请切回这个画面。");
        }
        overlayText = card.toString();
        MAIN.post(new Runnable() {
            @Override
            public void run() {
                refreshOverlay();
            }
        });
        try {
            File dir = diagnosticsDir(context);
            BppDiagnosticBundle.appendCapped(
                new File(dir, "game-heartbeat.log"), line, 1024 * 1024);
            StringBuilder state = new StringBuilder();
            state.append("stage=").append(stage).append('\n');
            state.append("label=").append(label).append('\n');
            state.append("elapsedSec=").append(elapsed).append('\n');
            state.append("interopFiles=").append(probe.interopFiles).append('\n');
            state.append("interopBytes=").append(probe.interopBytes).append('\n');
            state.append("unityLibFiles=").append(probe.unityLibFiles).append('\n');
            state.append("importance=").append(importance).append('\n');
            state.append("firstRun=").append(firstRun).append('\n');
            state.append("process=").append(processName).append('\n');
            state.append("pid=").append(android.os.Process.myPid()).append('\n');
            state.append("overlay=").append(overlayAttached).append('\n');
            state.append("foreground=").append(foregroundStarted ? 1 : 0).append('\n');
            writeAtomically(new File(dir, "generation-state.txt"), state.toString());
            File external = context.getExternalFilesDir(null);
            if (external != null) {
                BppDiagnosticBundle.appendIfRoom(
                    new File(external, "bepinex_trace.log"),
                    "[bpp-diag] " + line,
                    2 * 1024 * 1024
                );
            }
            BppDiagnosticBundle.appendIfRoom(
                new File(gameRoot, "BepInEx/logs/bpp-startup-trace.log"),
                "[bpp-diag] " + line,
                2 * 1024 * 1024
            );
        } catch (Throwable t) {
            logEvent(context, "publish diagnostics failed stage=" + stage, t);
        }
    }

    private static void patchConfigs(Context context, File gameRoot) {
        patchOne(context, new File(gameRoot, "BepInEx/config/BepInEx.cfg"));
        patchOne(context, new File(gameRoot, "modpacks/bpp/config/BepInEx.cfg"));
    }

    private static void patchOne(Context context, File cfg) {
        try {
            if (cfg == null || !cfg.isFile() || !cfg.canRead()) return;
            byte[] raw = Files.readAllBytes(cfg.toPath());
            if (raw.length > 1024 * 1024) return;
            String text = new String(raw, StandardCharsets.UTF_8);
            String updated = BppDiagnosticBundle.enableDiagnosticLogging(text);
            if (updated.equals(text)) return;
            writeAtomically(cfg, updated);
        } catch (Throwable t) {
            logEvent(context, "patch config failed file=" + (cfg == null ? "null" : cfg.getAbsolutePath()), t);
        }
    }

    private static boolean logMentionsPlugins(File log) {
        try {
            if (log == null || !log.isFile()) return false;
            long length = log.length();
            int take = (int) Math.min(length, 64 * 1024L);
            byte[] data = new byte[take];
            try (java.io.RandomAccessFile raf = new java.io.RandomAccessFile(log, "r")) {
                raf.seek(Math.max(0L, length - take));
                raf.readFully(data);
            }
            String tail = new String(data, StandardCharsets.UTF_8);
            return tail.contains("Chainloader startup complete")
                || tail.contains("Chainloader started")
                || tail.contains("plugin.initialization.succeeded")
                || tail.contains("Finished loading plugins");
        } catch (Throwable t) {
            Log.w(TAG, "logMentionsPlugins failed", t);
            return false;
        }
    }

    private static void dumpStacks(Context context, String stage, long elapsed) {
        try {
            StringBuilder sb = new StringBuilder();
            sb.append(BppDiagnosticBundle.nowIso())
                .append(" event=stall stage=").append(stage)
                .append(" elapsedSec=").append(elapsed).append('\n');
            sb.append(readSmall(new File("/proc/self/status"), 4000)).append('\n');
            File task = new File("/proc/self/task");
            String[] ids = task.list();
            if (ids == null) {
                sb.append("task=unreadable\n");
            } else {
                int shown = 0;
                for (int i = 0; i < ids.length && shown < 80; i++) {
                    File dir = new File(task, ids[i]);
                    sb.append("=== tid ").append(ids[i])
                        .append(" comm=").append(oneLine(readSmall(new File(dir, "comm"), 80)))
                        .append(" wchan=").append(oneLine(readSmall(new File(dir, "wchan"), 120)))
                        .append('\n');
                    sb.append(readSmall(new File(dir, "stack"), 1500)).append('\n');
                    shown++;
                }
            }
            Map<Thread, StackTraceElement[]> traces = Thread.getAllStackTraces();
            for (Map.Entry<Thread, StackTraceElement[]> entry : traces.entrySet()) {
                Thread thread = entry.getKey();
                sb.append("--- java ").append(thread.getName())
                    .append(" state=").append(thread.getState())
                    .append(" daemon=").append(thread.isDaemon()).append('\n');
                StackTraceElement[] frames = entry.getValue();
                int limit = Math.min(frames.length, 40);
                for (int i = 0; i < limit; i++) sb.append("  at ").append(frames[i]).append('\n');
            }
            File dir = diagnosticsDir(context);
            String name = "stacks-" + elapsed + "s.log";
            BppDiagnosticBundle.appendCapped(new File(dir, name), sb.toString(), 1024 * 1024);
        } catch (Throwable t) {
            logEvent(context, "dumpStacks failed stage=" + stage, t);
        }
    }

    static File gameRoot(Context context) {
        try {
            Class<?> paths = Class.forName("com.bepinex.android.BepInExPaths");
            Object instance = paths.getField("INSTANCE").get(null);
            Object dir = paths.getMethod("getGameRootDir", String.class)
                .invoke(instance, BppBundledModpackInstaller.TARGET_PACKAGE);
            if (dir instanceof File) return (File) dir;
        } catch (Throwable t) {
            Log.w(TAG, "gameRoot BepInExPaths reflection failed", t);
        }
        Context target = originalContext != null ? originalContext : context;
        File external = target.getExternalFilesDir(null);
        if (external == null) external = target.getFilesDir();
        return new File(external, "BepInEx_Android/" + BppBundledModpackInstaller.TARGET_PACKAGE);
    }

    private static int importance(Context context, int pid) {
        try {
            ActivityManager manager = (ActivityManager) context.getSystemService(Context.ACTIVITY_SERVICE);
            if (manager == null) return -1;
            List<ActivityManager.RunningAppProcessInfo> processes = manager.getRunningAppProcesses();
            if (processes == null) return -1;
            for (int i = 0; i < processes.size(); i++) {
                ActivityManager.RunningAppProcessInfo info = processes.get(i);
                if (info != null && info.pid == pid) return info.importance;
            }
        } catch (Throwable t) {
            Log.w(TAG, "importance failed pid=" + pid, t);
        }
        return -1;
    }

    private static long cpuTicks() {
        try {
            String stat = readSmall(new File("/proc/self/stat"), 2000);
            int paren = stat.lastIndexOf(')');
            if (paren < 0 || paren + 2 >= stat.length()) return -1L;
            String[] parts = stat.substring(paren + 2).trim().split("\\s+");
            if (parts.length < 13) return -1L;
            return Long.parseLong(parts[11]) + Long.parseLong(parts[12]);
        } catch (RuntimeException ex) {
            Log.w(TAG, "cpuTicks failed", ex);
            return -1L;
        }
    }

    private static long vmRssKb() {
        try {
            String status = readSmall(new File("/proc/self/status"), 4000);
            String[] lines = status.split("\n");
            for (int i = 0; i < lines.length; i++) {
                if (lines[i].startsWith("VmRSS:")) {
                    String digits = lines[i].replaceAll("[^0-9]", "");
                    if (digits.length() == 0) return -1L;
                    return Long.parseLong(digits);
                }
            }
        } catch (RuntimeException ex) {
            Log.w(TAG, "vmRssKb failed", ex);
            return -1L;
        }
        return -1L;
    }

    private static int readBaseline(Context context) {
        try {
            File file = new File(diagnosticsDir(context), "interop-baseline.txt");
            if (!file.isFile()) return 0;
            String text = readSmall(file, 40).trim();
            int value = Integer.parseInt(text);
            if (value < 0 || value > INTEROP_BASELINE_CAP) return 0;
            return value;
        } catch (RuntimeException ex) {
            Log.w(TAG, "readBaseline failed", ex);
            return 0;
        }
    }

    private static void writeBaseline(Context context, int files) {
        try {
            writeAtomically(
                new File(diagnosticsDir(context), "interop-baseline.txt"),
                Integer.toString(files)
            );
        } catch (Throwable t) {
            logEvent(context, "write baseline failed files=" + files, t);
        }
    }

    private static void writeAtomically(File file, String text) throws java.io.IOException {
        File parent = file.getParentFile();
        if (parent != null && !parent.isDirectory() && !parent.mkdirs()) {
            throw new java.io.IOException("cannot create " + parent);
        }
        File tmp = new File(file.getAbsolutePath() + ".tmp");
        try (FileOutputStream out = new FileOutputStream(tmp)) {
            out.write(text.getBytes(StandardCharsets.UTF_8));
            out.flush();
            out.getFD().sync();
        }
        if (!tmp.renameTo(file)) {
            try (FileOutputStream out = new FileOutputStream(file)) {
                out.write(text.getBytes(StandardCharsets.UTF_8));
                out.flush();
            }
            tmp.delete();
        }
    }

    private static String readSmall(File file, int maxBytes) {
        try {
            if (file == null || !file.isFile() || !file.canRead()) return "";
            byte[] data = Files.readAllBytes(file.toPath());
            if (data.length > maxBytes) {
                return new String(data, data.length - maxBytes, maxBytes, StandardCharsets.UTF_8);
            }
            return new String(data, StandardCharsets.UTF_8);
        } catch (Exception ex) {
            Log.w(TAG, "readSmall failed file=" + file, ex);
            return "";
        }
    }

    private static String oneLine(String value) {
        return value == null ? "" : value.replace('\n', ' ').replace('\r', ' ').trim();
    }

    private static String formatElapsed(long seconds) {
        long minutes = seconds / 60L;
        long rest = seconds % 60L;
        String sec = rest < 10 ? "0" + rest : Long.toString(rest);
        return minutes + ":" + sec;
    }

    private static String megabytes(long bytes) {
        if (bytes < 0) return "未知";
        return (bytes / (1024L * 1024L)) + " MB";
    }

    private static int dp(Context context, int value) {
        return Math.round(TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP, value, context.getResources().getDisplayMetrics()));
    }

    private static void logBuildInfoOnce(Context context) {
        synchronized (BppGameProcess.class) {
            if (buildInfoLogged) return;
            buildInfoLogged = true;
        }
        String line = BppBuildInfo.describe();
        Log.i(TAG, line);
        noteHookRaw(context, line);
    }

    private static void logBuildInfoLine(Context context, String line) {
        Log.i(TAG, line);
        noteHookRaw(context, line);
    }

    private static void noteHook(Context context, String event, String process) {
        try {
            String line = BppDiagnosticBundle.nowIso()
                + " event=" + oneLine(event)
                + " process=" + (process == null ? "" : process)
                + " pid=" + android.os.Process.myPid();
            File dir = diagnosticsDir(context);
            BppDiagnosticBundle.appendCapped(
                new File(dir, "hook-process.txt"), line, 64 * 1024);
        } catch (Throwable t) {
            Log.w(TAG, "noteHook failed event=" + event, t);
        }
    }

    private static void noteHookRaw(Context context, String line) {
        try {
            File dir = diagnosticsDir(context);
            BppDiagnosticBundle.appendCapped(
                new File(dir, "hook-process.txt"), line, 64 * 1024);
        } catch (Throwable t) {
            Log.w(TAG, "noteHookRaw failed", t);
        }
    }

    static File diagnosticsDir(Context context) {
        Context base = originalContext != null ? originalContext : context;
        File resolved = null;
        if (base != null) {
            try {
                resolved = BppDiagnosticSessionLog.diagnosticsDir(stableContext(base));
            } catch (Throwable t) {
                Log.w(TAG, "BppDiagnosticSessionLog.diagnosticsDir failed", t);
            }
        }
        if (resolved != null && isUnderLauncherData(resolved)) return resolved;
        Context original = originalContext;
        if (original == null && context != null && isLauncherContext(context)) original = context;
        if (original != null) {
            try {
                String dataDir = original.getApplicationInfo().dataDir;
                if (dataDir != null && isLauncherDataDir(dataDir)) {
                    return new File(dataDir, "files/" + DIAG_DIR);
                }
            } catch (Throwable t) {
                Log.w(TAG, "diagnosticsDir fallback dataDir failed", t);
            }
        }
        return new File("/data/user/0/" + LAUNCHER_PACKAGE + "/files/" + DIAG_DIR);
    }

    private static Context stableContext(final Context context) {
        if (context == null) return null;
        return new ContextWrapper(context) {
            @Override
            public Context getApplicationContext() {
                return this;
            }
        };
    }

    private static Context captureOriginalContext(Context context) {
        synchronized (BppGameProcess.class) {
            if (originalContext != null) return originalContext;
            if (context == null) return null;
            if (isLauncherContext(context)) {
                originalContext = context;
                return originalContext;
            }
            try {
                Context app = context.getApplicationContext();
                if (app != null && app != context && isLauncherContext(app)) {
                    originalContext = app;
                    return originalContext;
                }
            } catch (Throwable t) {
                Log.w(TAG, "captureOriginalContext getApplicationContext failed", t);
            }
            return null;
        }
    }

    private static boolean isLauncherContext(Context context) {
        if (context == null) return false;
        try {
            if (LAUNCHER_PACKAGE.equals(context.getPackageName())) return true;
        } catch (Throwable t) {
            Log.w(TAG, "isLauncherContext getPackageName failed", t);
        }
        try {
            android.content.pm.ApplicationInfo info = context.getApplicationInfo();
            if (info != null) {
                if (LAUNCHER_PACKAGE.equals(info.packageName)) return true;
                if (info.dataDir != null && isLauncherDataDir(info.dataDir)) return true;
            }
        } catch (Throwable t) {
            Log.w(TAG, "isLauncherContext getApplicationInfo failed", t);
        }
        return false;
    }

    private static boolean isLauncherDataDir(String path) {
        if (path == null) return false;
        return path.startsWith("/data/user/") && path.endsWith("/" + LAUNCHER_PACKAGE)
            || path.equals("/data/data/" + LAUNCHER_PACKAGE);
    }

    private static boolean isUnderLauncherData(File file) {
        if (file == null) return false;
        String path = file.getAbsolutePath();
        return path.startsWith("/data/user/")
                && path.indexOf("/" + LAUNCHER_PACKAGE + "/") >= 0
            || path.startsWith("/data/data/" + LAUNCHER_PACKAGE + "/")
            || path.equals("/data/data/" + LAUNCHER_PACKAGE);
    }

    private static String currentProcessName(Context context) {
        try {
            String process = Application.getProcessName();
            if (process != null && process.length() > 0) return process;
        } catch (Throwable t) {
            Log.w(TAG, "currentProcessName getProcessName failed", t);
        }
        try {
            byte[] data = Files.readAllBytes(new File("/proc/self/cmdline").toPath());
            int end = 0;
            while (end < data.length && data[end] != 0) end++;
            if (end > 0) return new String(data, 0, end, StandardCharsets.UTF_8);
        } catch (Throwable t) {
            Log.w(TAG, "currentProcessName read /proc/self/cmdline failed", t);
        }
        try {
            return context == null ? "" : context.getPackageName();
        } catch (Throwable t) {
            Log.w(TAG, "currentProcessName getPackageName failed", t);
            return "";
        }
    }

    private static boolean isGameProcess(String process) {
        return process != null && process.endsWith(":game");
    }

    private static boolean isBppLauncherActivity(Activity activity) {
        return activity != null
            && activity.getClass().getName().indexOf("BppLauncherActivity") >= 0;
    }

    private static String sourceFromStack() {
        StackTraceElement[] elements = new Throwable().getStackTrace();
        for (int i = 1; i < elements.length; i++) {
            String cls = elements[i].getClassName();
            if (cls.equals(BppGameProcess.class.getName())) continue;
            return cls + "." + elements[i].getMethodName();
        }
        return "unknown";
    }

    private static Context diagnosticsContext(Context fallback) {
        Context context = originalContext;
        if (context != null) return context;
        context = appContext;
        if (context != null) return context;
        return fallback;
    }

    private static Context contextFor(Activity activity) {
        Context context = originalContext;
        if (context != null) return context;
        if (activity != null) {
            try {
                Context app = activity.getApplicationContext();
                if (app != null) return app;
            } catch (Throwable t) {
                Log.w(TAG, "contextFor getApplicationContext failed", t);
            }
            return activity;
        }
        return appContext;
    }

    private static String safePackage(Context context) {
        if (context == null) return "null";
        try {
            return context.getPackageName();
        } catch (Throwable t) {
            Log.w(TAG, "safePackage failed", t);
            return "?";
        }
    }

    private static String hostName() {
        Activity activity = host.get();
        return activity == null ? "null" : activity.getClass().getName();
    }

    @Override public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) { return null; }
    @Override public String getType(Uri uri) { return null; }
    @Override public Uri insert(Uri uri, ContentValues values) { return null; }
    @Override public int delete(Uri uri, String selection, String[] selectionArgs) { return 0; }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) { return 0; }
}
