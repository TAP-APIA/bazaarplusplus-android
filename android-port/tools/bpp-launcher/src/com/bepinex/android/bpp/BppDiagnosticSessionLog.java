package com.bepinex.android.bpp;

import android.content.Context;
import java.io.File;
import java.io.IOException;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.TimeUnit;

/**
 * Persists a rolling logcat and launch breadcrumbs under the app files directory.
 * The launcher process and the {@code :game} process each write their own file so a grey
 * screen can still be exported after the user comes back.
 */
public final class BppDiagnosticSessionLog {

    public static final String DIR_NAME = "bpp-diagnostics";
    private static final int NOTE_CAP_BYTES = 256 * 1024;
    private static final Object GATE = new Object();
    private static final Set<String> STARTED = new HashSet<String>();
    private static final Map<String, String> MARKERS = new HashMap<String, String>();
    private static final ScheduledExecutorService SNAPSHOTS = Executors.newSingleThreadScheduledExecutor(
        new ThreadFactory() {
            @Override
            public Thread newThread(Runnable runnable) {
                Thread thread = new Thread(runnable, "bpp-diag-snapshots");
                thread.setDaemon(true);
                return thread;
            }
        }
    );
    private static final ScheduledExecutorService ROLLING = Executors.newSingleThreadScheduledExecutor(
        new ThreadFactory() {
            @Override
            public Thread newThread(Runnable runnable) {
                Thread thread = new Thread(runnable, "bpp-diag-rolling");
                thread.setDaemon(true);
                return thread;
            }
        }
    );

    private BppDiagnosticSessionLog() {}

    public static void ensureCapture(Context context, String role) {
        ensureCapture(context, role, 0);
    }

    /** {@code pid > 0} limits the rolling dump to that process ({@code logcat --pid}). */
    public static void ensureCapture(Context context, String role, int pid) {
        if (context == null || !isRole(role)) return;
        final Context app = application(context);
        final int filter = Math.max(0, pid);
        synchronized (GATE) {
            if (!STARTED.add(role)) return;
        }
        Thread thread = new Thread(new Runnable() {
            @Override
            public void run() {
                startRolling(app, role, filter);
            }
        }, "bpp-diag-" + role);
        thread.setDaemon(true);
        thread.start();
    }

    public static void note(Context context, String fileName, String line) {
        if (context == null || !isSafeFileName(fileName)) return;
        try {
            File dir = diagnosticsDir(context);
            if (!dir.isDirectory() && !dir.mkdirs()) return;
            BppDiagnosticBundle.appendCapped(new File(dir, fileName), line, NOTE_CAP_BYTES);
        } catch (Throwable ignored) {
        }
    }

    /** Records the launch attempt immediately, then dumps logcat again after the game has had time to start. */
    public static void noteLaunch(Context context, String line) {
        if (context == null) return;
        ensureCapture(context, "launcher");
        note(context, "launch-attempts.log", line);
        final Context app = application(context);
        scheduleDump(app, "launch-snapshot-2s.log", 2L);
        scheduleDump(app, "launch-snapshot-8s.log", 8L);
        scheduleDump(app, "launch-snapshot-20s.log", 20L);
    }

    public static File diagnosticsDir(Context context) {
        Context app = application(context);
        return new File(app.getFilesDir(), DIR_NAME);
    }

    private static void scheduleDump(final Context context, final String name, long delaySeconds) {
        SNAPSHOTS.schedule(new Runnable() {
            @Override
            public void run() {
                dumpTo(context, name);
            }
        }, delaySeconds, TimeUnit.SECONDS);
    }

    private static void dumpTo(Context context, String name) {
        if (!isSafeFileName(name)) return;
        try {
            File dir = diagnosticsDir(context);
            if (!dir.isDirectory() && !dir.mkdirs()) return;
            File target = new File(dir, name);
            File previous = new File(dir, "previous-" + name);
            synchronized (BppDiagnosticSessionLog.class) {
                if (target.isFile()) {
                    try {
                        Files.move(target.toPath(), previous.toPath(), java.nio.file.StandardCopyOption.REPLACE_EXISTING);
                    } catch (IOException ignored) {
                    }
                }
                String dump = dumpLogcat();
                BppDiagnosticBundle.appendCapped(target, dump, 4 * 1024 * 1024);
            }
        } catch (Throwable ignored) {
        }
    }

    static String dumpLogcat() {
        return dumpLogcat(0);
    }

    static String dumpLogcat(int pid) {
        String all = BppDiagnosticBundle.captureProcessText(
            BppDiagnosticBundle.dumpLogcatCommand("all", pid), 20000L, 3 * 1024 * 1024);
        if (!isFailedDump(all)) return all;
        StringBuilder sb = new StringBuilder();
        sb.append(all);
        if (!all.endsWith("\n")) sb.append('\n');
        for (int i = 0; i < BppDiagnosticBundle.LOGCAT_BUFFERS.length; i++) {
            String buffer = BppDiagnosticBundle.LOGCAT_BUFFERS[i];
            sb.append("===== buffer ").append(buffer).append(" =====\n");
            sb.append(BppDiagnosticBundle.captureProcessText(
                BppDiagnosticBundle.dumpLogcatCommand(buffer, pid), 12000L, 1024 * 1024));
            if (sb.length() > 4 * 1024 * 1024) break;
            sb.append('\n');
        }
        return sb.toString();
    }

    private static boolean isFailedDump(String text) {
        if (text == null || text.length() == 0) return true;
        if (text.startsWith("[unavailable]")) return true;
        String lower = text.toLowerCase(java.util.Locale.ROOT);
        boolean denied = lower.contains("permission denied") || lower.contains("unknown buffer");
        return denied && !lower.contains("beginning of");
    }

    private static void startRolling(final Context context, final String role, final int pid) {
        File dir = null;
        try {
            dir = diagnosticsDir(context);
            if (!dir.isDirectory() && !dir.mkdirs()) return;
            final File output = new File(dir, role + "-logcat");
            BppDiagnosticBundle.appendCapped(
                new File(dir, role + "-logcat.pid"),
                Integer.toString(pid > 0 ? pid : android.os.Process.myPid()),
                64
            );
            meta(dir, role, "capture-start mode=periodic-dump pidFilter=" + pid, output, "");
            ROLLING.scheduleAtFixedRate(new Runnable() {
                @Override
                public void run() {
                    captureIncremental(context, role, pid, output);
                }
            }, 0L, 2L, TimeUnit.SECONDS);
        } catch (Throwable error) {
            try {
                if (dir == null) dir = diagnosticsDir(context);
                if (dir.isDirectory() || dir.mkdirs()) {
                    meta(dir, role, "capture-failed error=" + oneLine(String.valueOf(error)), null, "");
                }
            } catch (Throwable ignored) {
            }
        }
    }

    private static void captureIncremental(Context context, String role, int pid, File output) {
        try {
            String dump = dumpLogcat(pid);
            if (isFailedDump(dump) && (dump == null || dump.indexOf('\n') < 0)) {
                File dir = diagnosticsDir(context);
                if (dir.isDirectory() || dir.mkdirs()) {
                    BppDiagnosticBundle.appendCapped(new File(dir, role + "-logcat.stderr"), oneLine(dump), 8000);
                }
                return;
            }
            String marker;
            synchronized (MARKERS) {
                marker = MARKERS.get(role);
            }
            String suffix = BppDiagnosticBundle.suffixAfterMarker(dump, marker);
            String nextMarker = BppDiagnosticBundle.lastLogLine(dump);
            if (nextMarker.length() > 0) {
                synchronized (MARKERS) {
                    MARKERS.put(role, nextMarker);
                }
            }
            if (suffix.length() == 0) return;
            BppDiagnosticBundle.appendCapped(output, suffix, 4 * 1024 * 1024);
        } catch (Throwable ignored) {
        }
    }

    private static void meta(File dir, String role, String event, File output, String stderr) throws IOException {
        StringBuilder line = new StringBuilder();
        line.append(BppDiagnosticBundle.nowIso())
            .append(" role=").append(role)
            .append(" event=").append(event)
            .append(" launcherPid=").append(android.os.Process.myPid());
        if (output != null) line.append(" file=").append(output.getAbsolutePath());
        if (stderr != null && stderr.length() > 0) line.append(" stderr=").append(oneLine(stderr));
        BppDiagnosticBundle.appendCapped(new File(dir, "session-meta.log"), line.toString(), NOTE_CAP_BYTES);
    }

    private static boolean logcatAlreadyRunning(String outputPath) {
        File proc = new File("/proc");
        String[] names = proc.list();
        if (names == null) return false;
        for (int i = 0; i < names.length; i++) {
            String name = names[i];
            if (!isPid(name)) continue;
            File cmdline = new File(new File(proc, name), "cmdline");
            try {
                byte[] raw = Files.readAllBytes(cmdline.toPath());
                String cmd = new String(raw, StandardCharsets.UTF_8).replace('\0', ' ');
                if (cmd.contains("logcat") && cmd.contains(outputPath)) return true;
            } catch (IOException ignored) {
            }
        }
        return false;
    }

    private static boolean isPid(String name) {
        if (name == null || name.length() == 0) return false;
        for (int i = 0; i < name.length(); i++) {
            char c = name.charAt(i);
            if (c < '0' || c > '9') return false;
        }
        return true;
    }

    private static int processPid(Process process) {
        try {
            Method method = process.getClass().getMethod("pid");
            Object value = method.invoke(process);
            if (value instanceof Integer) return ((Integer) value).intValue();
            if (value instanceof Long) return ((Long) value).intValue();
        } catch (Throwable ignored) {
        }
        try {
            Field field = process.getClass().getDeclaredField("pid");
            field.setAccessible(true);
            return field.getInt(process);
        } catch (Throwable ignored) {
        }
        return -1;
    }

    private static int exitValue(Process process) {
        try {
            return process.exitValue();
        } catch (IllegalThreadStateException ex) {
            return -1;
        }
    }

    private static String readSmall(File file, int maxBytes) {
        try {
            if (file == null || !file.isFile()) return "";
            byte[] data = Files.readAllBytes(file.toPath());
            if (data.length > maxBytes) {
                return new String(data, data.length - maxBytes, maxBytes, StandardCharsets.UTF_8);
            }
            return new String(data, StandardCharsets.UTF_8);
        } catch (IOException ex) {
            return "";
        }
    }

    private static boolean isRole(String role) {
        return "launcher".equals(role) || "game".equals(role);
    }

    static boolean isSafeFileName(String name) {
        if (name == null || name.length() == 0 || name.length() > 80) return false;
        if (name.indexOf('/') >= 0 || name.indexOf('\\') >= 0 || name.indexOf('\0') >= 0) return false;
        if (".".equals(name) || "..".equals(name) || name.contains("..")) return false;
        for (int i = 0; i < name.length(); i++) {
            char c = name.charAt(i);
            boolean ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || c == '.' || c == '_' || c == '-';
            if (!ok) return false;
        }
        return true;
    }

    private static Context application(Context context) {
        try {
            Context app = context.getApplicationContext();
            if (app != null) return app;
        } catch (Throwable ignored) {
        }
        return context;
    }

    private static String oneLine(String value) {
        return value == null ? "" : value.replace('\n', ' ').replace('\r', ' ');
    }
}
