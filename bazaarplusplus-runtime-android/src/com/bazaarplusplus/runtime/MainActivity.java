package com.bazaarplusplus.runtime;

import android.app.Activity;
import android.app.ActivityManager;
import android.app.ApplicationExitInfo;
import android.content.Intent;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.database.Cursor;
import android.graphics.Color;
import android.net.Uri;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.OpenableColumns;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;

import java.io.File;
import java.io.FileInputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;

public final class MainActivity extends Activity {
    private static final int REQUEST_APK = 1001;
    private TextView status;
    private TextView details;
    private Button importButton;
    private Button removeButton;
    private Button runtimeButton;
    private Button copyLogButton;
    private Button copySummaryButton;
    private Button exportLogButton;
    private ProgressBar progress;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        buildUi();
        refreshVersions();
    }

    private void buildUi() {
        int pad = dp(20);
        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(pad, pad, pad, pad);
        content.setBackgroundColor(Color.rgb(18, 14, 10));

        TextView title = text(26, Color.rgb(230, 205, 145));
        title.setText("BPP Runtime Probe");
        title.setGravity(Gravity.CENTER_HORIZONTAL);
        content.addView(title, params());
        TextView subtitle = text(15, Color.LTGRAY);
        subtitle.setText("第一阶段：导入并校验用户持有的官方 The Bazaar APK。当前版本不会启动游戏，也不会修改源 APK。");
        subtitle.setPadding(0, dp(12), 0, dp(18));
        content.addView(subtitle, params());

        status = text(18, Color.WHITE);
        content.addView(status, params());
        progress = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        progress.setMax(1000);
        progress.setVisibility(View.GONE);
        content.addView(progress, params());

        importButton = new Button(this);
        importButton.setText("选择官方 APK 并导入");
        importButton.setOnClickListener(v -> chooseApk());
        content.addView(importButton, params());
        runtimeButton = new Button(this);
        runtimeButton.setText("启动纯游戏 Runtime Probe");
        runtimeButton.setOnClickListener(v -> {
            RuntimeLogcatCapture.start(this);
            startActivity(new Intent(this, BazaarRuntimeActivity.class));
        });
        content.addView(runtimeButton, params());
        copyLogButton = new Button(this);
        copyLogButton.setText("复制 Runtime 日志");
        copyLogButton.setOnClickListener(v -> copyRuntimeLog());
        content.addView(copyLogButton, params());
        copySummaryButton = new Button(this);
        copySummaryButton.setText("复制关键诊断摘要");
        copySummaryButton.setOnClickListener(v -> copyKeySummary());
        content.addView(copySummaryButton, params());
        exportLogButton = new Button(this);
        exportLogButton.setText("导出完整日志到下载");
        exportLogButton.setOnClickListener(v -> exportLogs());
        content.addView(exportLogButton, params());
        removeButton = new Button(this);
        removeButton.setText("删除已导入版本");
        removeButton.setOnClickListener(v -> removeVersions());
        content.addView(removeButton, params());

        details = text(14, Color.rgb(210, 210, 210));
        details.setTextIsSelectable(true);
        details.setPadding(0, dp(18), 0, dp(30));
        content.addView(details, params());
        ScrollView scroll = new ScrollView(this);
        scroll.addView(content);
        setContentView(scroll);
    }

    private void chooseApk() {
        Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT);
        intent.addCategory(Intent.CATEGORY_OPENABLE);
        intent.setType("application/vnd.android.package-archive");
        intent.putExtra(Intent.EXTRA_MIME_TYPES, new String[]{
            "application/vnd.android.package-archive", "application/zip", "application/octet-stream"});
        startActivityForResult(intent, REQUEST_APK);
    }

    @Override protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != REQUEST_APK || resultCode != RESULT_OK || data == null || data.getData() == null) return;
        Uri uri = data.getData();
        try { getContentResolver().takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION); } catch (Exception ignored) {}
        beginImport(uri);
    }

    private void beginImport(Uri uri) {
        setBusy(true);
        long total = querySize(uri);
        status.setText("正在准备导入…");
        details.setText("文件：" + displayName(uri));
        new Thread(() -> {
            try {
                ApkImporter.Result result = ApkImporter.importApk(this, uri, total, (copied, expected, stage) ->
                    runOnUiThread(() -> updateProgress(copied, expected, stage)));
                runOnUiThread(() -> showSuccess(result));
            } catch (Exception ex) {
                runOnUiThread(() -> showFailure(ex));
            }
        }, "bpp-apk-import").start();
    }

    private void updateProgress(long copied, long total, String stage) {
        if ("inspect".equals(stage)) { status.setText("正在检查 APK 结构…"); progress.setIndeterminate(true); return; }
        status.setText("正在复制并计算 SHA-256：" + ApkImporter.human(copied) + (total > 0 ? " / " + ApkImporter.human(total) : ""));
        if (total > 0) { progress.setIndeterminate(false); progress.setProgress((int)Math.min(1000, copied * 1000 / total)); }
        else progress.setIndeterminate(true);
    }

    private void showSuccess(ApkImporter.Result r) {
        setBusy(false);
        status.setText("导入成功，Runtime Probe 尚未启动游戏");
        details.setText("包名：" + r.info.packageName
            + "\n版本：" + r.info.versionName + " (" + ApkImporter.versionCode(r.info) + ")"
            + "\n大小：" + ApkImporter.human(r.size)
            + "\nSHA-256：" + r.sha
            + "\n签名 SHA-256：" + ApkImporter.signingSha(r.info)
            + "\narm64-v8a：" + yes(r.probe.arm64)
            + "\nlibunity.so：" + yes(r.probe.libUnity)
            + "\nlibil2cpp.so：" + yes(r.probe.libIl2Cpp)
            + "\nUnity Data：" + yes(r.probe.unityData)
            + "\nglobal-metadata.dat：" + yes(r.probe.metadata)
            + "\n本地路径：" + r.apk.getAbsolutePath());
        removeButton.setVisibility(View.VISIBLE);
    }

    private void showFailure(Exception ex) {
        setBusy(false);
        ApkImporter.deleteRecursively(new File(getFilesDir(), "import-staging"));
        status.setText("导入失败");
        details.setText(ex.getClass().getSimpleName() + ": " + ex.getMessage());
    }

    private void setBusy(boolean busy) {
        importButton.setEnabled(!busy);
        runtimeButton.setEnabled(!busy && hasVersion());
        removeButton.setEnabled(!busy);
        progress.setVisibility(busy ? View.VISIBLE : View.GONE);
        if (busy) { progress.setIndeterminate(false); progress.setProgress(0); }
    }

    private void refreshVersions() {
        File[] children = new File(getFilesDir(), "versions").listFiles(File::isDirectory);
        if (children == null || children.length == 0) {
            status.setText("尚未导入");
            details.setText("请选择从官方渠道取得的 The Bazaar APK。");
            removeButton.setVisibility(View.GONE);
            runtimeButton.setEnabled(false);
            copyLogButton.setEnabled(RuntimeLog.file(this).isFile());
            return;
        }
        List<String> names = new ArrayList<>();
        for (File child : children) names.add(child.getName());
        Collections.sort(names);
        status.setText("已导入 " + names.size() + " 个版本");
        details.setText("版本 SHA-256：\n" + join(names));
        removeButton.setVisibility(View.VISIBLE);
        runtimeButton.setEnabled(true);
        copyLogButton.setEnabled(RuntimeLog.file(this).isFile());
        appendRuntimeLog();
    }

    private void removeVersions() {
        ApkImporter.deleteRecursively(new File(getFilesDir(), "versions"));
        ApkImporter.deleteRecursively(new File(getFilesDir(), "import-staging"));
        status.setText("已删除导入版本");
        details.setText("源 APK 不受影响。");
        removeButton.setVisibility(View.GONE);
        runtimeButton.setEnabled(false);
    }

    private void copyRuntimeLog() {
        String content = readRuntimeLog(200000);
        String logcat = readTail(RuntimeLogcatCapture.file(this), 60000);
        if (logcat.length() > 0) content += "\n\nRuntime logcat：\n" + logcat;
        String exit = latestExitInfo();
        if (exit.length() > 0) content += "\n\n最近 Runtime 退出信息：" + exit;
        if (content.length() == 0) { details.setText(details.getText() + "\n\n暂无 Runtime 日志或退出信息。"); return; }
        ClipboardManager clipboard = (ClipboardManager)getSystemService(Context.CLIPBOARD_SERVICE);
        clipboard.setPrimaryClip(ClipData.newPlainText("BPP Runtime Log", content));
        status.setText("Runtime 日志已复制到剪贴板");
    }

    private void copyKeySummary() {
        String content = DiagnosticExporter.keySummary(this, latestExitInfo());
        ClipboardManager clipboard = (ClipboardManager)getSystemService(Context.CLIPBOARD_SERVICE);
        clipboard.setPrimaryClip(ClipData.newPlainText("BPP Runtime Diagnostic Summary", content));
        status.setText("关键诊断摘要已复制");
    }

    private void exportLogs() {
        status.setText("正在导出完整日志…");
        new Thread(() -> {
            try {
                String name = DiagnosticExporter.exportToDownloads(this, latestExitInfo());
                runOnUiThread(() -> status.setText("已导出日志；若系统提供崩溃栈，下载中还会有同时间的 .trace 文件"));
            } catch (Exception ex) {
                runOnUiThread(() -> {
                    status.setText("导出失败");
                    details.setText(details.getText() + "\n\n日志导出失败：" + ex);
                });
            }
        }, "runtime-log-export").start();
    }

    @Override protected void onResume() {
        super.onResume();
        if (details != null && hasVersion()) {
            appendRuntimeLog();
            appendExitInfo();
            new Handler(Looper.getMainLooper()).postDelayed(this::appendExitInfo, 1500);
            if (RuntimeLogcatCapture.activeLongEnough()) {
                new Handler(Looper.getMainLooper()).postDelayed(RuntimeLogcatCapture::stop, 2000);
            }
        }
    }

    private void appendExitInfo() {
        String text = latestExitInfo();
        if (text.length() > 0 && !details.getText().toString().contains(text)) {
            details.setText(details.getText() + "\n\n最近 Runtime 退出信息：" + text);
        }
    }

    private String latestExitInfo() {
        if (android.os.Build.VERSION.SDK_INT < 30) return "";
        try {
            ActivityManager manager = (ActivityManager)getSystemService(Context.ACTIVITY_SERVICE);
            List<ApplicationExitInfo> exits = manager.getHistoricalProcessExitReasons(getPackageName(), 0, 8);
            StringBuilder text = new StringBuilder();
            for (ApplicationExitInfo exit : exits) {
                if (exit.getProcessName() == null || !exit.getProcessName().equals(ApkImporter.EXPECTED_PACKAGE)) continue;
                text.append("\n时间：").append(new SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS", Locale.ROOT).format(new Date(exit.getTimestamp())));
                text.append("\n进程：").append(exit.getProcessName());
                text.append("\nreason=").append(exit.getReason());
                text.append(" status=").append(exit.getStatus());
                text.append(" importance=").append(exit.getImportance());
                text.append("\n描述：").append(exit.getDescription());
                break;
            }
            return text.toString();
        } catch (Exception ex) {
            return "\n读取失败：" + ex;
        }
    }

    private boolean hasVersion() {
        File[] children = new File(getFilesDir(), "versions").listFiles(File::isDirectory);
        return children != null && children.length > 0;
    }

    private void appendRuntimeLog() {
        String content = readRuntimeLog(12000);
        copyLogButton.setEnabled(content.length() > 0);
        if (content.length() > 0) details.setText(details.getText() + "\n\n最近 Runtime 日志：\n" + content);
    }

    private String readRuntimeLog(int limit) {
        return readTail(RuntimeLog.file(this), limit);
    }

    private String readTail(File log, int limit) {
        if (!log.isFile()) return "";
        try (FileInputStream input = new FileInputStream(log)) {
            long length = log.length();
            long skip = Math.max(0, length - limit);
            while (skip > 0) { long done = input.skip(skip); if (done <= 0) break; skip -= done; }
            byte[] bytes = new byte[(int)Math.min(limit, length)];
            int count = input.read(bytes);
            return count > 0 ? new String(bytes, 0, count, StandardCharsets.UTF_8) : "";
        } catch (Exception ex) { return "读取日志失败：" + ex; }
    }

    private long querySize(Uri uri) {
        try (Cursor c = getContentResolver().query(uri, new String[]{OpenableColumns.SIZE}, null, null, null)) {
            if (c != null && c.moveToFirst() && !c.isNull(0)) return c.getLong(0);
        } catch (Exception ignored) {}
        return -1;
    }
    private String displayName(Uri uri) {
        try (Cursor c = getContentResolver().query(uri, new String[]{OpenableColumns.DISPLAY_NAME}, null, null, null)) {
            if (c != null && c.moveToFirst()) return c.getString(0);
        } catch (Exception ignored) {}
        return String.valueOf(uri);
    }
    private TextView text(int size, int color) { TextView v = new TextView(this); v.setTextSize(size); v.setTextColor(color); return v; }
    private LinearLayout.LayoutParams params() { return new LinearLayout.LayoutParams(-1, -2); }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private static String yes(boolean value) { return value ? "是" : "否"; }
    private static String join(List<String> values) { StringBuilder b = new StringBuilder(); for (String v : values) { if (b.length() > 0) b.append('\n'); b.append(v); } return b.toString(); }
}
