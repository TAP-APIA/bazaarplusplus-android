package com.bazzarhelper.android;

import android.app.Application;
import android.content.Context;
import android.util.Log;
import java.io.*;
import java.text.SimpleDateFormat;
import java.util.*;

/**
 * Small rolling file log, one per process. It records the events we would otherwise only see in
 * logcat (which a non-rooted phone cannot read), so the home screen can export them.
 */
public final class Diag {
  private static final String TAG = "BazaarHelper";
  private static final long MAX_BYTES = 256 * 1024;
  private static final Object LOCK = new Object();
  private static final SimpleDateFormat STAMP =
      new SimpleDateFormat("MM-dd HH:mm:ss.SSS", Locale.US);

  private static File file;

  private Diag() {}

  public static void init(Context context) {
    if (file != null) return;
    String process = Application.getProcessName();
    file =
        new File(
            context.getFilesDir(), process != null && process.endsWith(":game") ? "game.log" : "app.log");
    Thread.UncaughtExceptionHandler previous = Thread.getDefaultUncaughtExceptionHandler();
    Thread.setDefaultUncaughtExceptionHandler(
        (thread, error) -> {
          e("crash", "uncaught in " + thread.getName(), error);
          if (previous != null) previous.uncaughtException(thread, error);
        });
    i("app", "process started · " + process);
  }

  public static File file() {
    return file;
  }

  public static void i(String event, String message) {
    write("I", event, message, null);
  }

  public static void e(String event, String message, Throwable error) {
    write("E", event, message, error);
  }

  private static void write(String level, String event, String message, Throwable error) {
    Log.println(
        "E".equals(level) ? Log.ERROR : Log.INFO, TAG, event + ": " + (message == null ? "" : message));
    if (error != null) Log.println(Log.ERROR, TAG, Log.getStackTraceString(error));
    File target = file;
    if (target == null) return;
    synchronized (LOCK) {
      try {
        byte[] line =
            (STAMP.format(new Date())
                    + " "
                    + level
                    + " "
                    + event
                    + ": "
                    + (message == null ? "" : message)
                    + (error == null ? "" : "\n" + Log.getStackTraceString(error))
                    + "\n")
                .getBytes("UTF-8");
        if (target.length() + line.length > MAX_BYTES) rotate(target);
        try (FileOutputStream out = new FileOutputStream(target, true)) {
          out.write(line);
        }
      } catch (IOException ignored) {
        // Diagnostics never break the feature they describe.
      }
    }
  }

  private static void rotate(File target) {
    File backup = new File(target.getPath() + ".1");
    //noinspection ResultOfMethodCallIgnored
    backup.delete();
    //noinspection ResultOfMethodCallIgnored
    target.renameTo(backup);
  }

  /** Tail of a log file, newest last, capped so an export stays shareable. */
  public static String tail(File source, int maxBytes) {
    if (source == null || !source.isFile()) return "(无)";
    try (RandomAccessFile raf = new RandomAccessFile(source, "r")) {
      long length = raf.length();
      long from = Math.max(0, length - maxBytes);
      byte[] buffer = new byte[(int) (length - from)];
      raf.seek(from);
      raf.readFully(buffer);
      String text = new String(buffer, "UTF-8");
      return from > 0 ? "…(更早的内容已省略)\n" + text : text;
    } catch (IOException e) {
      return "(读取失败: " + e.getMessage() + ")";
    }
  }
}
