package com.bazaarplusplus.runtime;

import android.content.Context;
import android.util.Log;
import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;

final class RuntimeLog {
    private static final String TAG = "BppRuntime";
    static synchronized void write(Context context, String message) {
        String line = new SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS", Locale.ROOT).format(new Date()) + " " + message + "\n";
        Log.i(TAG, message);
        try {
            File file = new File(context.getFilesDir(), "runtime-probe.log");
            try (FileOutputStream output = new FileOutputStream(file, true)) {
                output.write(line.getBytes(StandardCharsets.UTF_8));
            }
        } catch (Exception ex) {
            Log.e(TAG, "log write failed", ex);
        }
    }
    static File file(Context context) { return new File(context.getFilesDir(), "runtime-probe.log"); }
    static void reset(Context context) { File file = file(context); if (file.exists()) file.delete(); }
}

