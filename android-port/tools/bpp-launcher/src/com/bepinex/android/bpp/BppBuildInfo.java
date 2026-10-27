package com.bepinex.android.bpp;

import android.app.Application;
import android.util.Log;
import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;

/** Build metadata injected by the packaging script. */
public final class BppBuildInfo {

    private static final String TAG = "BppGame";

    public static final String COMMIT = "dev";
    public static final String VERSION_NAME = "dev";

    private BppBuildInfo() {}

    /** Same line format used by the :game process start log. */
    public static String describe() {
        return "BuildInfo version=" + VERSION_NAME
            + " commit=" + COMMIT
            + " process=" + processName()
            + " pid=" + android.os.Process.myPid();
    }

    private static String processName() {
        try {
            String process = Application.getProcessName();
            if (process != null && process.length() > 0) return process;
        } catch (Throwable t) {
            Log.w(TAG, "BuildInfo getProcessName failed", t);
        }
        try {
            byte[] data = Files.readAllBytes(new File("/proc/self/cmdline").toPath());
            int end = 0;
            while (end < data.length && data[end] != 0) end++;
            if (end > 0) return new String(data, 0, end, StandardCharsets.UTF_8);
        } catch (Throwable t) {
            Log.w(TAG, "BuildInfo read /proc/self/cmdline failed", t);
        }
        return "unknown";
    }
}
