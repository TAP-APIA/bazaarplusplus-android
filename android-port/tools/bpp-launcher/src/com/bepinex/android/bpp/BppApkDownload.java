package com.bepinex.android.bpp;

import android.app.DownloadManager;
import android.content.Context;
import android.content.SharedPreferences;
import android.content.pm.PackageInfo;
import android.database.Cursor;
import android.net.Uri;
import android.os.Environment;
import java.io.File;
import java.io.IOException;

/** One persisted system download, surviving launcher recreation and process death. */
public final class BppApkDownload {
    private final Context context;
    private final DownloadManager manager;
    private final SharedPreferences preferences;

    public BppApkDownload(Context context) {
        this.context = context;
        manager = (DownloadManager) context.getSystemService(Context.DOWNLOAD_SERVICE);
        preferences = context.getSharedPreferences("bpp-apk-update", Context.MODE_PRIVATE);
    }

    public boolean hasDownload() { return preferences.getLong("id", -1) != -1; }

    public void start(BppLauncherUpdateChecker.Update update) throws IOException {
        if (hasDownload()) return;
        if (manager == null) throw new IOException("系统下载服务不可用");
        File directory = context.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS);
        if (directory == null) throw new IOException("下载目录不可用");
        File target = new File(directory, "bpp-update.apk");
        if (target.exists() && !target.delete()) throw new IOException("无法清理旧安装包");
        DownloadManager.Request request = new DownloadManager.Request(Uri.parse(update.apkUrl))
            .setTitle("BPP Android " + update.version)
            .setDescription("下载完成后，请返回 BPP 点击安装")
            .setMimeType("application/vnd.android.package-archive")
            .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
            .setDestinationInExternalFilesDir(context, Environment.DIRECTORY_DOWNLOADS, target.getName());
        long id = manager.enqueue(request);
        if (!preferences.edit().putLong("id", id).putString("version", update.version)
                .putLong("size", update.apkSize).commit()) {
            manager.remove(id);
            throw new IOException("无法保存下载任务，请重试");
        }
    }

    public static final class State {
        public final boolean complete;
        public final String message;
        State(boolean complete, String message) { this.complete = complete; this.message = message; }
    }

    public State state() throws IOException {
        if (manager == null) throw new IOException("系统下载服务不可用");
        try (Cursor cursor = manager.query(new DownloadManager.Query().setFilterById(preferences.getLong("id", -1)))) {
            if (cursor == null || !cursor.moveToFirst()) throw new IOException("下载任务已被移除，请重试");
            int status = cursor.getInt(cursor.getColumnIndexOrThrow(DownloadManager.COLUMN_STATUS));
            if (status == DownloadManager.STATUS_FAILED) {
                int reason = cursor.getInt(cursor.getColumnIndexOrThrow(DownloadManager.COLUMN_REASON));
                throw new IOException("下载失败（" + reason + "），请重试或从 GitHub 下载");
            }
            if (status == DownloadManager.STATUS_SUCCESSFUL)
                return new State(true, "下载完成 · 点击安装 " + preferences.getString("version", ""));
            long received = cursor.getLong(cursor.getColumnIndexOrThrow(DownloadManager.COLUMN_BYTES_DOWNLOADED_SO_FAR));
            long total = cursor.getLong(cursor.getColumnIndexOrThrow(DownloadManager.COLUMN_TOTAL_SIZE_BYTES));
            String progress = total > 0 ? (received * 100 / total) + "%" : (received / 1024 / 1024) + " MB";
            return new State(false, (status == DownloadManager.STATUS_PAUSED ? "下载暂停，等待网络 · " : "正在下载 · ") + progress);
        }
    }

    /** Recheck the file on every install attempt; the system installer verifies signing continuity. */
    public Uri installationUri() throws Exception {
        if (!state().complete) throw new IOException("安装包尚未下载完成");
        File directory = context.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS);
        if (directory == null) throw new IOException("下载目录不可用");
        File apk = new File(directory, "bpp-update.apk");
        if (!apk.isFile() || apk.length() != preferences.getLong("size", -1))
            throw new IOException("安装包大小不符，请重新下载");
        PackageInfo archive = context.getPackageManager().getPackageArchiveInfo(apk.getAbsolutePath(), 0);
        PackageInfo installed = context.getPackageManager().getPackageInfo(context.getPackageName(), 0);
        if (archive == null || !context.getPackageName().equals(archive.packageName)
                || !preferences.getString("version", "").equals(archive.versionName)
                || archive.getLongVersionCode() <= installed.getLongVersionCode())
            throw new IOException("安装包不是当前应用的新版本，请重新检查更新");
        Uri uri = manager.getUriForDownloadedFile(preferences.getLong("id", -1));
        if (uri == null || !"content".equals(uri.getScheme())) throw new IOException("无法读取安装包，请重新下载");
        return uri;
    }

    public boolean alreadyInstalled(String current) {
        String downloaded = preferences.getString("version", "");
        try { return hasDownload() && BppLauncherUpdateChecker.compareVersions(current, downloaded) >= 0; }
        catch (IllegalArgumentException ignored) { return false; }
    }

    public void clear() {
        long id = preferences.getLong("id", -1);
        if (manager != null && id != -1) manager.remove(id);
        preferences.edit().clear().apply();
    }
}
