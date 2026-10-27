package com.bepinex.android.bpp;

import android.app.Activity;
import android.app.ActivityManager;
import android.app.Application;
import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
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
import android.util.TypedValue;
import android.view.Gravity;
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
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.List;
import java.util.Map;

/**
 * Heartbeat and the first-run progress card for {@code :game}.
 * The provider entry is only a backup: on the vivo that shipped preview.40 the provider's
 * {@code android:process} was ignored and {@code onCreate} ran in the launcher, so the card
 * never met the Unity window. {@link BppGameApplication} calls {@link #start} from whichever
 * process is actually running, and {@code start} returns unless that process is {@code :game}.
 */
public final class BppGameProcess extends ContentProvider {

    private static final long HEARTBEAT_MS = 3000L;
    private static final long STALL_MS = 20000L;
    private static final long WAKE_LOCK_MS = 30L * 60L * 1000L;
    private static final int INTEROP_BASELINE_CAP = 20000;

    private static volatile boolean pluginsReady;
    private static volatile boolean started;
    private static volatile boolean foregroundStarted;
    private static volatile int overlayAttached;
    private static volatile String overlayText = "";
    private static volatile String processName = "";
    private static volatile int interopBaseline;
    private static final Handler MAIN = new Handler(Looper.getMainLooper());
    private static final Runnable MAINTAIN = new Runnable() {
        @Override
        public void run() {
            if (pluginsReady) return;
            Activity activity = host.get();
            if (activity != null && !activity.isFinishing() && !activity.isDestroyed()) {
                attach(activity);
            }
            MAIN.postDelayed(this, 1000L);
        }
    };
    private static WeakReference<Activity> host = new WeakReference<Activity>(null);
    private static Context appContext;
    private static View overlayView;
    private static WindowManager overlayWm;
    private static boolean overlayInWindow;
    private static TextView bodyView;
    private static PowerManager.WakeLock generationLock;

    @Override
    public boolean onCreate() {
        try {
            Context context = getContext();
            if (context != null) start(context);
        } catch (Throwable ignored) {
        }
        return true;
    }

    /** Starts the overlay and heartbeat only when the caller is the real {@code :game} process. */
    public static void start(Context context) {
        try {
            if (context == null) return;
            Context resolved = context.getApplicationContext();
            if (resolved == null) resolved = context;
            final Context app = resolved;
            String process = Application.getProcessName();
            if (process == null || !process.endsWith(":game")) {
                noteHook(app, "game-hook-skipped", process);
                return;
            }
            if (!markStarted()) return;
            appContext = app;
            processName = process;
            noteHook(app, "game-hook-started", process);
            final long startedAt = System.currentTimeMillis();
            final int pid = android.os.Process.myPid();
            BppDiagnosticSessionLog.ensureCapture(app, "game", pid);
            registerLifecycle(app);
            holdWakeLock(app);
            Thread thread = new Thread(new Runnable() {
                @Override
                public void run() {
                    heartbeat(app, startedAt, pid);
                }
            }, "bpp-game-heartbeat");
            thread.setDaemon(true);
            thread.start();
            MAIN.removeCallbacks(MAINTAIN);
            MAIN.post(MAINTAIN);
        } catch (Throwable ignored) {
        }
    }

    public static void markPluginsReady() {
        pluginsReady = true;
        MAIN.removeCallbacks(MAINTAIN);
        MAIN.post(new Runnable() {
            @Override
            public void run() {
                detach();
                stopForegroundHold();
                releaseWakeLock();
            }
        });
        Context context = currentContext();
        if (context != null) {
            try {
                File state = new File(BppDiagnosticSessionLog.diagnosticsDir(context), "generation-state.txt");
                writeAtomically(state, "stage=ready\nlabel=" + BppDiagnosticBundle.generationStageLabel("ready") + "\n");
            } catch (Throwable ignored) {
            }
        }
    }

    private static boolean markStarted() {
        synchronized (BppGameProcess.class) {
            if (started) return false;
            started = true;
            return true;
        }
    }

    private static Context currentContext() {
        Activity activity = host.get();
        if (activity != null) return activity;
        return appContext;
    }

    private static void noteHook(Context context, String event, String process) {
        try {
            String line = BppDiagnosticBundle.nowIso()
                + " event=" + event
                + " process=" + (process == null ? "" : process)
                + " pid=" + android.os.Process.myPid();
            File dir = BppDiagnosticSessionLog.diagnosticsDir(context);
            BppDiagnosticBundle.appendCapped(new File(dir, "hook-process.txt"), line, 64 * 1024);
        } catch (Throwable ignored) {
        }
    }

    private static void registerLifecycle(Context context) {
        Context appContext = context.getApplicationContext();
        if (!(appContext instanceof Application)) return;
        ((Application) appContext).registerActivityLifecycleCallbacks(new Application.ActivityLifecycleCallbacks() {
            @Override
            public void onActivityResumed(Activity activity) {
                considerAttach(activity);
            }

            @Override
            public void onActivityDestroyed(Activity activity) {
                if (host.get() == activity) detach();
            }

            @Override public void onActivityCreated(Activity activity, Bundle savedInstanceState) {}
            @Override public void onActivityStarted(Activity activity) {}
            @Override public void onActivityPaused(Activity activity) {}
            @Override public void onActivityStopped(Activity activity) {}
            @Override public void onActivitySaveInstanceState(Activity activity, Bundle outState) {}
        });
    }

    private static void considerAttach(final Activity activity) {
        if (activity == null || pluginsReady) return;
        if (activity.getClass().getName().indexOf("BppLauncherActivity") >= 0) return;
        MAIN.post(new Runnable() {
            @Override
            public void run() {
                attach(activity);
            }
        });
    }

    private static void attach(Activity activity) {
        if (pluginsReady || activity == null || activity.isFinishing() || activity.isDestroyed()) return;
        try {
            activity.getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
            View decor = activity.getWindow().getDecorView();
            sinkSurfaces(decor);
            if (overlayView != null && host.get() == activity && overlayAttached == 1 && overlayView.isAttachedToWindow()) {
                refreshOverlay();
                ensureForeground(activity);
                return;
            }
            detachView();
            FrameLayout root = buildOverlay(activity);
            IBinder token = decor.getWindowToken();
            boolean added = token != null && addPanel(activity, root, token);
            if (!added) added = addToContent(activity, root);
            if (!added) return;
            overlayView = root;
            host = new WeakReference<Activity>(activity);
            overlayAttached = 1;
            refreshOverlay();
            ensureForeground(activity);
        } catch (Throwable ignored) {
        }
    }

    /** Unity's SurfaceView is composited in its own window and can cover a normal dialog. */
    private static void sinkSurfaces(View view) {
        if (view instanceof SurfaceView) {
            SurfaceView surface = (SurfaceView) view;
            try {
                surface.setZOrderOnTop(false);
            } catch (Throwable ignored) {
            }
            try {
                surface.setZOrderMediaOverlay(false);
            } catch (Throwable ignored) {
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
                params.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS;
            }
            manager.addView(root, params);
            overlayWm = manager;
            overlayInWindow = true;
            return true;
        } catch (Throwable ignored) {
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
        } catch (Throwable ignored) {
            return false;
        }
    }

    private static void ensureForeground(Activity activity) {
        if (foregroundStarted || pluginsReady || activity == null) return;
        if (!needsForegroundHold(activity)) return;
        try {
            Intent intent = new Intent(activity, BppGenerationForegroundService.class);
            activity.startForegroundService(intent);
            foregroundStarted = true;
        } catch (Throwable ignored) {
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
        } catch (Throwable ignored) {
            return true;
        }
    }

    private static void stopForegroundHold() {
        if (!foregroundStarted) return;
        Context context = currentContext();
        if (context == null) return;
        try {
            Intent intent = new Intent(context, BppGenerationForegroundService.class);
            intent.setAction(BppGenerationForegroundService.ACTION_STOP);
            context.startService(intent);
        } catch (Throwable ignored) {
        }
        foregroundStarted = false;
    }

    private static void holdWakeLock(Context context) {
        try {
            if (generationLock != null && generationLock.isHeld()) return;
            PowerManager manager = (PowerManager) context.getSystemService(Context.POWER_SERVICE);
            if (manager == null) return;
            PowerManager.WakeLock lock = manager.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "bpp:generation");
            lock.setReferenceCounted(false);
            lock.acquire(WAKE_LOCK_MS);
            generationLock = lock;
        } catch (Throwable ignored) {
        }
    }

    private static void releaseWakeLock() {
        try {
            PowerManager.WakeLock lock = generationLock;
            generationLock = null;
            if (lock != null && lock.isHeld()) lock.release();
        } catch (Throwable ignored) {
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

    private static void detach() {
        detachView();
        host = new WeakReference<Activity>(null);
    }

    private static void detachView() {
        View view = overlayView;
        WindowManager manager = overlayWm;
        boolean inWindow = overlayInWindow;
        overlayView = null;
        overlayWm = null;
        overlayInWindow = false;
        overlayAttached = 0;
        bodyView = null;
        if (view == null) return;
        try {
            if (inWindow && manager != null) manager.removeViewImmediate(view);
            else if (view.getParent() instanceof ViewGroup) ((ViewGroup) view.getParent()).removeView(view);
        } catch (Throwable ignored) {
        }
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
        while (!pluginsReady) {
            try {
                long now = System.currentTimeMillis();
                BppDiagnosticBundle.GenerationProbe probe = new BppDiagnosticBundle.GenerationProbe();
                probe.interopFilesAtStart = interopAtStart;
                File unityLibs = new File(gameRoot, "BepInEx/unity-libs");
                BppDiagnosticBundle.DirStat unity = BppDiagnosticBundle.summarizeTree(unityLibs, null, 1);
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
                    && (logOutput.lastModified() > logMtimeAtStart || logOutput.length() != logAtStart);
                probe.logIsNew = logNew;
                probe.logOutputBytes = logNew ? logOutput.length() : 0L;
                String stage = BppDiagnosticBundle.generationStageId(probe);
                if (!stage.equals(lastStage)) {
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
                String label = BppDiagnosticBundle.generationStageLabel(stage);
                long elapsed = Math.max(0L, (now - startedAt) / 1000L);
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
                patchConfigs(gameRoot);
                boolean stalled = now - lastAdvanceAt >= STALL_MS && now - lastCpuAt >= STALL_MS;
                if (stalled && !dumpedStall && !"ready".equals(stage)) {
                    dumpedStall = true;
                    dumpStacks(context, stage, elapsed);
                }
                if (logNew && logMentionsPlugins(logOutput)) {
                    markPluginsReady();
                    break;
                }
                if (probe.interopFiles > baseline) {
                    baseline = probe.interopFiles;
                    interopBaseline = baseline;
                    writeBaseline(context, baseline);
                }
            } catch (Throwable ignored) {
            }
            try {
                Thread.sleep(HEARTBEAT_MS);
            } catch (InterruptedException interrupted) {
                Thread.currentThread().interrupt();
                return;
            }
        }
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
            || (probe.interopFilesAtStart <= 0 && !"plugins".equals(stage) && !"ready".equals(stage));
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
        if (probe.unityZipBytes > 0) card.append("，压缩包 ").append(megabytes(probe.unityZipBytes));
        card.append('\n');
        card.append("interop：").append(probe.interopFiles).append(" 个文件，")
            .append(megabytes(probe.interopBytes)).append('\n');
        if (probe.dummyFiles > 0) card.append("Cpp2IL 临时文件：").append(probe.dummyFiles).append(" 个\n");
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
            File dir = BppDiagnosticSessionLog.diagnosticsDir(context);
            BppDiagnosticBundle.appendCapped(new File(dir, "game-heartbeat.log"), line, 1024 * 1024);
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
                BppDiagnosticBundle.appendIfRoom(new File(external, "bepinex_trace.log"), "[bpp-diag] " + line, 2 * 1024 * 1024);
            }
            BppDiagnosticBundle.appendIfRoom(
                new File(gameRoot, "BepInEx/logs/bpp-startup-trace.log"),
                "[bpp-diag] " + line,
                2 * 1024 * 1024
            );
        } catch (Throwable ignored) {
        }
    }

    private static void patchConfigs(File gameRoot) {
        patchOne(new File(gameRoot, "BepInEx/config/BepInEx.cfg"));
        patchOne(new File(gameRoot, "modpacks/bpp/config/BepInEx.cfg"));
    }

    private static void patchOne(File cfg) {
        try {
            if (cfg == null || !cfg.isFile() || !cfg.canRead()) return;
            byte[] raw = Files.readAllBytes(cfg.toPath());
            if (raw.length > 1024 * 1024) return;
            String text = new String(raw, StandardCharsets.UTF_8);
            String updated = BppDiagnosticBundle.enableDiagnosticLogging(text);
            if (updated.equals(text)) return;
            writeAtomically(cfg, updated);
        } catch (Throwable ignored) {
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
                || tail.contains("plugin.initialization.succeeded")
                || tail.contains("Finished loading plugins");
        } catch (Throwable ignored) {
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
            if (ids == null) sb.append("task=unreadable\n");
            else {
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
            File dir = BppDiagnosticSessionLog.diagnosticsDir(context);
            String name = "stacks-" + elapsed + "s.log";
            BppDiagnosticBundle.appendCapped(new File(dir, name), sb.toString(), 1024 * 1024);
        } catch (Throwable ignored) {
        }
    }

    static File gameRoot(Context context) {
        try {
            Class<?> paths = Class.forName("com.bepinex.android.BepInExPaths");
            Object instance = paths.getField("INSTANCE").get(null);
            Object dir = paths.getMethod("getGameRootDir", String.class)
                .invoke(instance, BppBundledModpackInstaller.TARGET_PACKAGE);
            if (dir instanceof File) return (File) dir;
        } catch (Throwable ignored) {
        }
        File external = context.getExternalFilesDir(null);
        if (external == null) external = context.getFilesDir();
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
        } catch (Throwable ignored) {
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
            return -1L;
        }
        return -1L;
    }

    private static int readBaseline(Context context) {
        try {
            File file = new File(BppDiagnosticSessionLog.diagnosticsDir(context), "interop-baseline.txt");
            if (!file.isFile()) return 0;
            String text = readSmall(file, 40).trim();
            int value = Integer.parseInt(text);
            if (value < 0 || value > INTEROP_BASELINE_CAP) return 0;
            return value;
        } catch (RuntimeException ex) {
            return 0;
        }
    }

    private static void writeBaseline(Context context, int files) {
        try {
            writeAtomically(
                new File(BppDiagnosticSessionLog.diagnosticsDir(context), "interop-baseline.txt"),
                Integer.toString(files)
            );
        } catch (Throwable ignored) {
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

    @Override public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) { return null; }
    @Override public String getType(Uri uri) { return null; }
    @Override public Uri insert(Uri uri, ContentValues values) { return null; }
    @Override public int delete(Uri uri, String selection, String[] selectionArgs) { return 0; }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) { return 0; }
}
