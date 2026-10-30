package com.bazaarplusplus.runtime;

import android.content.Context;
import java.io.BufferedReader;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;

final class RuntimeLogcatCapture {
    private static volatile Process process;
    private static volatile long startedAt;

    static synchronized void start(Context context) {
        stop();
        File output = file(context);
        if (output.exists()) output.delete();
        try {
            process = new ProcessBuilder("logcat", "-v", "threadtime", "-T", "1").redirectErrorStream(true).start();
            startedAt = android.os.SystemClock.elapsedRealtime();
            Process captured = process;
            new Thread(() -> {
                try (BufferedReader reader = new BufferedReader(new InputStreamReader(captured.getInputStream()));
                     FileOutputStream stream = new FileOutputStream(output, true)) {
                    String line;
                    while ((line = reader.readLine()) != null) {
                        String lower = line.toLowerCase();
                        if (lower.contains("inventory=") || lower.contains("view method ")
                            || lower.contains("view field ") || lower.contains("constructor candidate=")) continue;
                        if (line.contains("BppRuntime") || line.contains("bazaarplusplus.runtime")
                            || line.contains("Unity") || line.contains("libunity") || line.contains("libil2cpp")
                            || line.contains("Fatal signal") || line.contains("Sending signal")
                            || lower.contains("crash_dump") || lower.contains("tombstone")) {
                            stream.write((line + "\n").getBytes(StandardCharsets.UTF_8));
                            stream.flush();
                        }
                    }
                } catch (Exception ignored) {}
            }, "runtime-logcat-reader").start();
        } catch (Exception ex) {
            RuntimeLog.write(context, "logcat capture failed: " + ex);
        }
    }

    static synchronized void stop() {
        Process active = process;
        process = null;
        if (active != null) active.destroy();
    }

    static boolean activeLongEnough() {
        return process != null && android.os.SystemClock.elapsedRealtime() - startedAt > 500;
    }

    static File file(Context context) { return new File(context.getFilesDir(), "runtime-logcat.txt"); }
}
