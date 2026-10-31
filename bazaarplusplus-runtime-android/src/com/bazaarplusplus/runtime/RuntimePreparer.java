package com.bazaarplusplus.runtime;

import android.content.Context;
import android.system.Os;
import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.Enumeration;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

final class RuntimePreparer {
    static void prepare(Context context, GameVersion version) throws Exception {
        // Android 14+ rejects writable files passed to DexClassLoader. The APK remains
        // deletable because its parent directory is writable, but its contents must be read-only.
        Os.chmod(version.apk.getAbsolutePath(), 0444);
        if (version.apk.canWrite()) throw new Exception("source.apk 仍可写，无法安全加载 DEX");
        RuntimeLog.write(context, "source APK chmod=0444 writable=" + version.apk.canWrite());
        if (!version.nativeDir.mkdirs() && !version.nativeDir.isDirectory()) throw new Exception("无法创建 native 目录");
        if (!version.optimizedDir.mkdirs() && !version.optimizedDir.isDirectory()) throw new Exception("无法创建 dex-cache 目录");
        int extracted = 0;
        try (ZipFile zip = new ZipFile(version.apk)) {
            Enumeration<? extends ZipEntry> entries = zip.entries();
            while (entries.hasMoreElements()) {
                ZipEntry entry = entries.nextElement();
                String prefix = "lib/arm64-v8a/";
                if (entry.isDirectory() || !entry.getName().startsWith(prefix) || !entry.getName().endsWith(".so")) continue;
                String name = entry.getName().substring(prefix.length());
                File destination = new File(version.nativeDir, name);
                if (destination.isFile() && destination.length() == entry.getSize()) continue;
                File temporary = new File(version.nativeDir, name + ".tmp");
                copy(zip.getInputStream(entry), temporary);
                if (entry.getSize() >= 0 && temporary.length() != entry.getSize()) throw new Exception("native 库复制不完整：" + name);
                if (destination.exists() && !destination.delete()) throw new Exception("无法替换 native 库：" + name);
                if (!temporary.renameTo(destination)) throw new Exception("无法提交 native 库：" + name);
                destination.setReadable(true, true);
                extracted++;
            }
        }
        File[] libraries = version.nativeDir.listFiles((dir, name) -> name.endsWith(".so"));
        int readOnly = 0;
        if (libraries != null) {
            for (File library : libraries) {
                Os.chmod(library.getAbsolutePath(), 0444);
                if (!library.canWrite()) readOnly++;
            }
        }
        RuntimeLog.write(context, "native prepare complete extracted=" + extracted + " readOnly=" + readOnly + " dir=" + version.nativeDir);
    }

    private static void copy(InputStream raw, File destination) throws Exception {
        byte[] buffer = new byte[1024 * 1024];
        try (InputStream input = new BufferedInputStream(raw, buffer.length); OutputStream output = new BufferedOutputStream(new FileOutputStream(destination), buffer.length)) {
            int read; while ((read = input.read(buffer)) >= 0) { if (read > 0) output.write(buffer, 0, read); }
        }
    }
}

