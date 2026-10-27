package com.bepinex.android.bpp;

import android.app.Application;
import android.content.Context;
import android.system.Os;
import android.system.OsConstants;
import android.util.Log;
import java.io.File;
import java.io.FileDescriptor;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.RandomAccessFile;
import java.nio.charset.StandardCharsets;
import java.time.Instant;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;

/**
 * Diagnostic-only stdout/stderr redirect and JIT summary capture for the :game process.
 * Must run before BepInEx starts CoreCLR so the PAL sees the environment.
 */
public final class BppJitTrace {

    private static final String TAG = "BppGame";
    private static final String DIR_NAME = "bpp-jit";
    private static final int TAIL_CHECK_BYTES = 64 * 1024;
    private static final Object LOCK = new Object();

    private static boolean sInstalled;
    private static volatile File sTraceDir;
    private static FileDescriptor sStdoutFd;
    private static FileDescriptor sStderrFd;

    private BppJitTrace() {}

    public static void install(Context base) {
        if (base == null) return;
        String processName;
        try {
            processName = Application.getProcessName();
        } catch (Throwable error) {
            logError(base, "process name unavailable", error);
            return;
        }
        if (processName == null || !processName.endsWith(":game")) {
            return;
        }
        synchronized (LOCK) {
            if (sInstalled) {
                log(base, "already installed");
                return;
            }
            sInstalled = true;
        }
        try {
            log(base, "install begin process=" + processName);
            File dir = prepareTraceDir(base);
            sTraceDir = dir;
            log(base, "trace dir=" + dir.getAbsolutePath());
            rotateLogs(base, dir);
            pruneNetTraces(base, dir);
            setEnv(base, dir);

            File stdout = new File(dir, "jit-stdout.log");
            File stderr = new File(dir, "coreclr-stderr.log");
            FileDescriptor out = openAppend(base, stdout);
            FileDescriptor err = openAppend(base, stderr);
            sStdoutFd = out;
            sStderrFd = err;

            writeHeader(base, out, headerLine("stdout"));
            writeHeader(base, err, headerLine("stderr"));

            Os.dup2(out, 1);
            log(base, "dup2 fd1 -> " + stdout.getAbsolutePath());
            Os.dup2(err, 2);
            log(base, "dup2 fd2 -> " + stderr.getAbsolutePath());

            writeEnvSnapshot(base, dir);
            startChecks(base, dir);
            log(base, "install complete");
        } catch (Throwable error) {
            logError(base, "install failed", error);
        }
    }

    public static File traceDir(Context context) {
        File cached = sTraceDir;
        if (cached != null) return cached;
        if (context == null) return null;
        try {
            File dir = prepareTraceDir(context);
            sTraceDir = dir;
            return dir;
        } catch (Throwable error) {
            logError(context, "trace dir unavailable", error);
            return null;
        }
    }

    private static File prepareTraceDir(Context context) {
        File external = context.getExternalFilesDir(null);
        if (external != null) {
            File candidate = new File(external, DIR_NAME);
            if (ensureDir(context, candidate)) return candidate;
        }
        File files = context.getFilesDir();
        if (files != null) {
            File candidate = new File(files, DIR_NAME);
            if (ensureDir(context, candidate)) return candidate;
        }
        throw new IllegalStateException("cannot create " + DIR_NAME);
    }

    private static boolean ensureDir(Context context, File dir) {
        if (dir.isDirectory()) return true;
        boolean made = dir.mkdirs();
        if (made || dir.isDirectory()) {
            log(context, "created dir " + dir.getAbsolutePath());
            return true;
        }
        log(context, "mkdirs failed " + dir.getAbsolutePath());
        return false;
    }

    private static void rotateLogs(Context context, File dir) {
        rotateOne(context, new File(dir, "jit-stdout.log"), new File(dir, "jit-stdout.prev.log"));
        rotateOne(context, new File(dir, "coreclr-stderr.log"), new File(dir, "coreclr-stderr.prev.log"));
    }

    private static void rotateOne(Context context, File current, File previous) {
        if (previous.exists()) {
            boolean deleted = previous.delete();
            log(context, "delete old " + previous.getAbsolutePath() + " ok=" + deleted);
        }
        if (!current.exists()) {
            log(context, "rotate missing " + current.getAbsolutePath());
            return;
        }
        boolean renamed = current.renameTo(previous);
        if (renamed) {
            log(context, "rotated " + current.getName() + " -> " + previous.getName());
            return;
        }
        try {
            copyFile(current, previous);
            boolean deleted = current.delete();
            log(context, "rotate copy fallback " + current.getName() + " deleted=" + deleted);
        } catch (Throwable error) {
            logError(context, "rotate failed " + current.getAbsolutePath(), error);
        }
    }

    private static void copyFile(File from, File to) throws Exception {
        try (FileInputStream in = new FileInputStream(from);
             FileOutputStream out = new FileOutputStream(to)) {
            byte[] buf = new byte[65536];
            int n;
            while ((n = in.read(buf)) != -1) out.write(buf, 0, n);
            out.getFD().sync();
        }
    }

    private static void pruneNetTraces(Context context, File dir) {
        File[] files = dir.listFiles();
        if (files == null) {
            log(context, "nettrace list unreadable");
            return;
        }
        List<File> traces = new ArrayList<File>();
        for (int i = 0; i < files.length; i++) {
            File file = files[i];
            if (file.isFile() && file.getName().toLowerCase(Locale.ROOT).endsWith(".nettrace")) {
                traces.add(file);
            }
        }
        Collections.sort(traces, new Comparator<File>() {
            @Override
            public int compare(File a, File b) {
                int time = Long.compare(b.lastModified(), a.lastModified());
                return time != 0 ? time : a.getName().compareTo(b.getName());
            }
        });
        for (int i = 2; i < traces.size(); i++) {
            File file = traces.get(i);
            long bytes = file.length();
            boolean deleted = file.delete();
            log(context, "prune nettrace " + file.getName() + " bytes=" + bytes + " deleted=" + deleted);
        }
    }

    private static void setEnv(Context context, File dir) {
        setOne(context, "DOTNET_JitDisasmSummary", "1");
        unsetOne(context, "DOTNET_JitStdOutFile");
        setOne(context, "DOTNET_EnableEventPipe", "1");
        setOne(context, "DOTNET_EventPipeOutputPath", new File(dir, "jit-{pid}.nettrace").getAbsolutePath());
        setOne(context, "DOTNET_EventPipeConfig", "Microsoft-Windows-DotNETRuntime:0x10:5");
        setOne(context, "DOTNET_EventPipeOutputStreaming", "1");
    }

    private static void setOne(Context context, String name, String value) {
        try {
            Os.setenv(name, value, true);
            log(context, "setenv " + name + "=" + value);
        } catch (Throwable error) {
            logError(context, "setenv failed " + name, error);
        }
    }

    private static void unsetOne(Context context, String name) {
        try {
            Os.unsetenv(name);
            log(context, "unsetenv " + name);
        } catch (Throwable error) {
            logError(context, "unsetenv failed " + name, error);
        }
    }

    private static FileDescriptor openAppend(Context context, File file) throws Exception {
        int flags = OsConstants.O_WRONLY | OsConstants.O_CREAT | OsConstants.O_APPEND;
        FileDescriptor fd = Os.open(file.getAbsolutePath(), flags, 0644);
        log(context, "open append " + file.getAbsolutePath());
        return fd;
    }

    private static void writeHeader(Context context, FileDescriptor fd, String header) throws Exception {
        byte[] data = (header + "\n").getBytes(StandardCharsets.UTF_8);
        int offset = 0;
        while (offset < data.length) {
            int written = Os.write(fd, data, offset, data.length - offset);
            if (written <= 0) break;
            offset += written;
        }
        log(context, "header written bytes=" + offset);
    }

    private static String headerLine(String stream) {
        return "# BppJitTrace " + stream
            + " time=" + DateTimeFormatter.ISO_INSTANT.format(Instant.now())
            + " pid=" + android.os.Process.myPid()
            + " version=" + BppBuildInfo.VERSION_NAME
            + " commit=" + BppBuildInfo.COMMIT;
    }

    private static void writeEnvSnapshot(Context context, File dir) throws Exception {
        StringBuilder sb = new StringBuilder();
        sb.append("# ").append(headerLine("env")).append('\n');
        sb.append("DOTNET_JitDisasmSummary=1\n");
        sb.append("DOTNET_JitStdOutFile=<unset>\n");
        sb.append("DOTNET_EnableEventPipe=1\n");
        sb.append("DOTNET_EventPipeOutputPath=").append(new File(dir, "jit-{pid}.nettrace").getAbsolutePath()).append('\n');
        sb.append("DOTNET_EventPipeConfig=Microsoft-Windows-DotNETRuntime:0x10:5\n");
        sb.append("DOTNET_EventPipeOutputStreaming=1\n");
        sb.append("/proc/self/fd/1=").append(readLinkSafe("/proc/self/fd/1")).append('\n');
        sb.append("/proc/self/fd/2=").append(readLinkSafe("/proc/self/fd/2")).append('\n');
        appendText(context, new File(dir, "jit-trace-env.txt"), sb.toString());
        log(context, "env snapshot written fd1=" + readLinkSafe("/proc/self/fd/1")
            + " fd2=" + readLinkSafe("/proc/self/fd/2"));
    }

    private static void startChecks(final Context context, final File dir) {
        Thread thread = new Thread(new Runnable() {
            @Override
            public void run() {
                long[] waits = new long[] {3000L, 7000L, 20000L, 90000L};
                long elapsed = 0L;
                for (int i = 0; i < waits.length; i++) {
                    try {
                        Thread.sleep(waits[i]);
                    } catch (InterruptedException error) {
                        logError(context, "checks interrupted", error);
                        return;
                    }
                    elapsed += waits[i];
                    try {
                        check(context, dir, elapsed);
                    } catch (Throwable error) {
                        logError(context, "check failed at " + elapsed + "ms", error);
                    }
                }
            }
        }, "bpp-jit-trace-check");
        thread.setDaemon(true);
        thread.start();
        log(context, "scheduled checks at 3s, 10s, 30s, 120s");
    }

    private static void check(Context context, File dir, long elapsedMs) throws Exception {
        StringBuilder sb = new StringBuilder();
        String fd1 = readLinkSafe("/proc/self/fd/1");
        String fd2 = readLinkSafe("/proc/self/fd/2");
        sb.append("--- check at ").append(elapsedMs).append("ms time=")
            .append(DateTimeFormatter.ISO_INSTANT.format(Instant.now())).append('\n');
        sb.append("/proc/self/fd/1=").append(fd1).append('\n');
        sb.append("/proc/self/fd/2=").append(fd2).append('\n');
        appendFileInfo(sb, dir, "jit-stdout.log");
        appendFileInfo(sb, dir, "coreclr-stderr.log");
        File[] files = dir.listFiles();
        if (files == null) {
            sb.append("dir.listFiles=null\n");
        } else {
            for (int i = 0; i < files.length; i++) {
                File file = files[i];
                if (file.isFile() && file.getName().toLowerCase(Locale.ROOT).endsWith(".nettrace")) {
                    sb.append("nettrace ").append(file.getName())
                        .append(" bytes=").append(file.length()).append('\n');
                }
            }
        }
        String last = readLastLine(new File(dir, "jit-stdout.log"), TAIL_CHECK_BYTES);
        sb.append("jit-stdout.last=").append(last).append('\n');
        appendText(context, new File(dir, "jit-trace-env.txt"), sb.toString());
        log(context, "check at " + elapsedMs + "ms fd1=" + fd1 + " fd2=" + fd2);
    }

    private static void appendFileInfo(StringBuilder sb, File dir, String name) {
        File file = new File(dir, name);
        sb.append(name).append(" exists=").append(file.exists())
            .append(" bytes=").append(file.isFile() ? file.length() : -1L).append('\n');
    }

    private static String readLastLine(File file, int maxBytes) {
        if (file == null || !file.isFile() || file.length() == 0L) return "(empty)";
        long length = file.length();
        int cap = (int) Math.min(length, (long) Math.max(1, maxBytes));
        byte[] data = new byte[cap];
        try (RandomAccessFile raf = new RandomAccessFile(file, "r")) {
            raf.seek(length - cap);
            int offset = 0;
            while (offset < cap) {
                int n = raf.read(data, offset, cap - offset);
                if (n < 0) break;
                offset += n;
            }
            String text = new String(data, 0, offset, StandardCharsets.UTF_8);
            int end = text.length();
            while (end > 0 && (text.charAt(end - 1) == '\n' || text.charAt(end - 1) == '\r')) end--;
            if (end == 0) return "(blank)";
            int start = text.lastIndexOf('\n', end - 1);
            start = start < 0 ? 0 : start + 1;
            String line = text.substring(start, end);
            if (line.length() > 2000) line = line.substring(0, 2000) + "...(truncated)";
            return line;
        } catch (Throwable error) {
            return "unavailable: " + error.getClass().getSimpleName() + ": " + error.getMessage();
        }
    }

    private static String readLinkSafe(String path) {
        try {
            return Os.readlink(path);
        } catch (Throwable error) {
            return "unavailable: " + error.getClass().getSimpleName() + ": " + error.getMessage();
        }
    }

    private static void appendText(Context context, File file, String text) throws Exception {
        File parent = file.getParentFile();
        if (parent != null && !parent.isDirectory() && !parent.mkdirs()) {
            throw new IllegalStateException("cannot create " + parent.getAbsolutePath());
        }
        try (FileOutputStream out = new FileOutputStream(file, true)) {
            out.write(text.getBytes(StandardCharsets.UTF_8));
            out.flush();
            out.getFD().sync();
        }
    }

    private static void log(Context context, String message) {
        Log.i(TAG, message);
        try {
            BppGameProcess.logEvent(context, "BppJitTrace: " + message);
        } catch (Throwable error) {
            Log.e(TAG, "logEvent failed", error);
        }
    }

    private static void logError(Context context, String message, Throwable error) {
        Log.e(TAG, message, error);
        try {
            BppGameProcess.logEvent(context, "BppJitTrace: " + message, error);
        } catch (Throwable second) {
            Log.e(TAG, "logEvent failed", second);
        }
    }
}
