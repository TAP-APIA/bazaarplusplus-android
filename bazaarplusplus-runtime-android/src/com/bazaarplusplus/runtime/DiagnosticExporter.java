package com.bazaarplusplus.runtime;

import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.Context;
import android.app.ActivityManager;
import android.app.ApplicationExitInfo;
import android.net.Uri;
import android.os.Environment;
import android.provider.MediaStore;
import java.io.BufferedReader;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.FileReader;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.List;

final class DiagnosticExporter {
    static String keySummary(Context context, String exitInfo) {
        StringBuilder result = new StringBuilder();
        appendMatches(RuntimeLog.file(context), result);
        appendMatches(RuntimeLogcatCapture.file(context), result);
        if (exitInfo != null && !exitInfo.isEmpty()) result.append("\nEXIT INFO").append(exitInfo).append('\n');
        return result.length() == 0 ? "未找到关键诊断行" : result.toString();
    }

    private static void appendMatches(File file, StringBuilder output) {
        if (!file.isFile()) return;
        output.append("\n--- ").append(file.getName()).append(" ---\n");
        try (BufferedReader reader = new BufferedReader(new FileReader(file))) {
            String line;
            while ((line = reader.readLine()) != null) {
                String lower = line.toLowerCase(Locale.ROOT);
                if (lower.contains("activity oncreate")
                    || lower.contains("cmdline=")
                    || lower.contains("native prepare")
                    || lower.contains("controller constructed")
                    || lower.contains("unity state stage=")
                    || lower.contains("render view attached")
                    || lower.contains("unityplayer.resume")
                    || lower.contains("windowfocuschanged")
                    || lower.contains("snapshot view=")
                    || lower.contains("sending signal")
                    || lower.contains("fatal signal")
                    || lower.contains("sigkill")
                    || lower.contains("sigsegv")
                    || lower.contains("crash_dump")
                    || lower.contains("tombstone")
                    || lower.contains("process") && lower.contains("died")
                    || lower.contains("construction failed")
                    || lower.contains("prepare failed")) {
                    output.append(line).append('\n');
                }
            }
        } catch (Exception ex) {
            output.append("读取失败：").append(ex).append('\n');
        }
    }

    static String exportToDownloads(Context context, String exitInfo) throws Exception {
        String stamp = new SimpleDateFormat("yyyyMMdd-HHmmss", Locale.ROOT).format(new Date());
        String name = "BPP-runtime-diagnostics-" + stamp + ".txt";
        ContentValues values = new ContentValues();
        values.put(MediaStore.Downloads.DISPLAY_NAME, name);
        values.put(MediaStore.Downloads.MIME_TYPE, "text/plain");
        values.put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS);
        values.put(MediaStore.Downloads.IS_PENDING, 1);
        ContentResolver resolver = context.getContentResolver();
        Uri uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values);
        if (uri == null) throw new Exception("MediaStore 无法创建下载文件");
        try {
            try (OutputStream output = resolver.openOutputStream(uri)) {
                if (output == null) throw new Exception("无法打开下载文件");
                output.write(("BazaarPlusPlus Runtime Probe diagnostics\n"
                    + "time=" + new Date() + "\n"
                    + "summary:\n" + keySummary(context, exitInfo) + "\n\n").getBytes(StandardCharsets.UTF_8));
                appendFile(RuntimeLog.file(context), output);
                appendFile(RuntimeLogcatCapture.file(context), output);
            }
            values.clear();
            values.put(MediaStore.Downloads.IS_PENDING, 0);
            resolver.update(uri, values, null, null);
            exportExitTrace(context, resolver, stamp);
            return name;
        } catch (Exception ex) {
            resolver.delete(uri, null, null);
            throw ex;
        }
    }

    private static void exportExitTrace(Context context, ContentResolver resolver, String stamp) {
        if (android.os.Build.VERSION.SDK_INT < 30) return;
        Uri uri = null;
        try {
            ActivityManager manager = (ActivityManager)context.getSystemService(Context.ACTIVITY_SERVICE);
            List<ApplicationExitInfo> exits = manager.getHistoricalProcessExitReasons(context.getPackageName(), 0, 8);
            ApplicationExitInfo selected = null;
            for (ApplicationExitInfo exit : exits) {
                if (ApkImporter.EXPECTED_PACKAGE.equals(exit.getProcessName())) { selected = exit; break; }
            }
            if (selected == null) return;
            InputStream trace = selected.getTraceInputStream();
            if (trace == null) return;
            ContentValues traceValues = new ContentValues();
            traceValues.put(MediaStore.Downloads.DISPLAY_NAME, "BPP-runtime-exit-" + stamp + ".trace");
            traceValues.put(MediaStore.Downloads.MIME_TYPE, "application/octet-stream");
            traceValues.put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS);
            traceValues.put(MediaStore.Downloads.IS_PENDING, 1);
            uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, traceValues);
            if (uri == null) { trace.close(); return; }
            try (InputStream input = trace; OutputStream output = resolver.openOutputStream(uri)) {
                if (output == null) throw new Exception("无法打开 trace 下载文件");
                byte[] buffer = new byte[64 * 1024];
                int read; while ((read = input.read(buffer)) >= 0) if (read > 0) output.write(buffer, 0, read);
            }
            traceValues.clear();
            traceValues.put(MediaStore.Downloads.IS_PENDING, 0);
            resolver.update(uri, traceValues, null, null);
        } catch (Exception ex) {
            if (uri != null) resolver.delete(uri, null, null);
            RuntimeLog.write(context, "exit trace export failed: " + ex);
        }
    }

    private static void appendFile(File file, OutputStream output) throws Exception {
        output.write(("\n\n===== " + file.getName() + " =====\n").getBytes(StandardCharsets.UTF_8));
        if (!file.isFile()) { output.write("(missing)\n".getBytes(StandardCharsets.UTF_8)); return; }
        byte[] buffer = new byte[64 * 1024];
        try (InputStream input = new FileInputStream(file)) {
            int read; while ((read = input.read(buffer)) >= 0) if (read > 0) output.write(buffer, 0, read);
        }
    }
}
