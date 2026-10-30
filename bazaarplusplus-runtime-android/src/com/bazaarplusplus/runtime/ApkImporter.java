package com.bazaarplusplus.runtime;

import android.content.Context;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.pm.Signature;
import android.net.Uri;
import android.os.Build;
import android.os.StatFs;
import android.system.Os;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.security.MessageDigest;
import java.util.Enumeration;
import java.util.Locale;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

final class ApkImporter {
    static final String EXPECTED_PACKAGE = "com.tempogames.thebazaar";

    interface Progress { void update(long copied, long total, String stage); }

    static Result importApk(Context context, Uri uri, long expectedSize, Progress progress) throws Exception {
        File versions = new File(context.getFilesDir(), "versions");
        File staging = new File(context.getFilesDir(), "import-staging");
        deleteRecursively(staging);
        if (!staging.mkdirs() && !staging.isDirectory()) throw new Exception("无法创建导入暂存目录");
        File stagedApk = new File(staging, "source.apk");

        long free = new StatFs(context.getFilesDir().getAbsolutePath()).getAvailableBytes();
        if (expectedSize > 0 && free < expectedSize + 512L * 1024L * 1024L) {
            throw new Exception("空间不足：APK " + human(expectedSize) + "，可用 " + human(free) + "；需额外保留 512 MB");
        }

        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        long copied = 0;
        byte[] buffer = new byte[1024 * 1024];
        InputStream rawInput = context.getContentResolver().openInputStream(uri);
        if (rawInput == null) throw new Exception("系统未返回可读取的文件流");
        try (InputStream input = new BufferedInputStream(rawInput, buffer.length);
             OutputStream output = new BufferedOutputStream(new FileOutputStream(stagedApk), buffer.length)) {
            int read;
            while ((read = input.read(buffer)) >= 0) {
                if (read == 0) continue;
                output.write(buffer, 0, read);
                digest.update(buffer, 0, read);
                copied += read;
                progress.update(copied, expectedSize, "copy");
            }
        }
        if (expectedSize > 0 && copied != expectedSize) {
            throw new Exception("复制不完整：预期 " + expectedSize + " 字节，实际 " + copied);
        }

        String sha = hex(digest.digest());
        progress.update(copied, expectedSize, "inspect");
        PackageInfo info = context.getPackageManager().getPackageArchiveInfo(
            stagedApk.getAbsolutePath(), PackageManager.GET_SIGNING_CERTIFICATES);
        if (info == null) throw new Exception("Android 无法解析该 APK 的包信息");
        if (!EXPECTED_PACKAGE.equals(info.packageName)) {
            throw new Exception("包名不匹配：" + info.packageName + "；需要 " + EXPECTED_PACKAGE);
        }

        Probe probe = inspectZip(stagedApk);
        if (!probe.arm64) throw new Exception("APK 不包含 arm64-v8a native 库");
        if (!probe.libUnity) throw new Exception("APK 中未找到 libunity.so");
        if (!probe.libIl2Cpp) throw new Exception("APK 中未找到 libil2cpp.so");
        if (!probe.unityData) throw new Exception("APK 中未识别到 Unity Data");

        File destination = new File(versions, sha);
        if (destination.isDirectory()) {
            deleteRecursively(staging);
        } else {
            if (!versions.mkdirs() && !versions.isDirectory()) throw new Exception("无法创建版本目录");
            if (!staging.renameTo(destination)) throw new Exception("无法原子提交导入版本");
        }
        File committedApk = new File(destination, "source.apk");
        Os.chmod(committedApk.getAbsolutePath(), 0444);
        return new Result(sha, copied, info, probe, committedApk);
    }

    private static Probe inspectZip(File apk) throws Exception {
        Probe result = new Probe();
        try (ZipFile zip = new ZipFile(apk)) {
            Enumeration<? extends ZipEntry> entries = zip.entries();
            while (entries.hasMoreElements()) {
                String name = entries.nextElement().getName().toLowerCase(Locale.ROOT);
                if (name.startsWith("lib/arm64-v8a/")) result.arm64 = true;
                if (name.equals("lib/arm64-v8a/libunity.so")) result.libUnity = true;
                if (name.equals("lib/arm64-v8a/libil2cpp.so")) result.libIl2Cpp = true;
                if (name.endsWith("/global-metadata.dat")) result.metadata = true;
                if (name.startsWith("assets/bin/data/") || name.endsWith("globalgamemanagers") || name.contains("/data.unity3d")) {
                    result.unityData = true;
                }
            }
        }
        return result;
    }

    static String signingSha(PackageInfo info) {
        try {
            Signature[] signatures = info.signingInfo == null ? null : info.signingInfo.getApkContentsSigners();
            if (signatures == null || signatures.length == 0) return "不可用";
            return hex(MessageDigest.getInstance("SHA-256").digest(signatures[0].toByteArray()));
        } catch (Exception ex) {
            return "读取失败：" + ex.getMessage();
        }
    }

    static long versionCode(PackageInfo info) {
        return Build.VERSION.SDK_INT >= 28 ? info.getLongVersionCode() : info.versionCode;
    }

    static String human(long bytes) {
        if (bytes < 0) return "未知";
        if (bytes < 1000) return bytes + " B";
        if (bytes < 1000L * 1000L) return String.format(Locale.ROOT, "%.2f KB", bytes / 1000.0);
        if (bytes < 1000L * 1000L * 1000L) return String.format(Locale.ROOT, "%.2f MB", bytes / 1000000.0);
        return String.format(Locale.ROOT, "%.2f GB (%.2f GiB)", bytes / 1000000000.0, bytes / 1073741824.0);
    }

    static String hex(byte[] bytes) {
        StringBuilder result = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) result.append(String.format(Locale.ROOT, "%02x", value & 0xff));
        return result.toString();
    }

    static void deleteRecursively(File file) {
        if (file == null || !file.exists()) return;
        File[] children = file.listFiles();
        if (children != null) for (File child : children) deleteRecursively(child);
        file.delete();
    }

    static final class Probe { boolean arm64; boolean libUnity; boolean libIl2Cpp; boolean metadata; boolean unityData; }
    static final class Result {
        final String sha; final long size; final PackageInfo info; final Probe probe; final File apk;
        Result(String sha, long size, PackageInfo info, Probe probe, File apk) {
            this.sha = sha; this.size = size; this.info = info; this.probe = probe; this.apk = apk;
        }
    }
}

