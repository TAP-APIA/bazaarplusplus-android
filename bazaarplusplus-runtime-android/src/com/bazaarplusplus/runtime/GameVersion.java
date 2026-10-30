package com.bazaarplusplus.runtime;

import android.content.Context;
import java.io.File;
import java.util.Arrays;
import java.util.Comparator;

final class GameVersion {
    final File root;
    final File apk;
    final File nativeDir;
    final File optimizedDir;

    private GameVersion(File root) {
        this.root = root;
        this.apk = new File(root, "source.apk");
        this.nativeDir = new File(root, "native/arm64-v8a");
        this.optimizedDir = new File(root, "dex-cache");
    }

    static GameVersion newest(Context context) throws Exception {
        File[] roots = new File(context.getFilesDir(), "versions").listFiles(File::isDirectory);
        if (roots == null || roots.length == 0) throw new Exception("尚未导入游戏版本");
        Arrays.sort(roots, Comparator.comparingLong(File::lastModified).reversed());
        GameVersion version = new GameVersion(roots[0]);
        if (!version.apk.isFile()) throw new Exception("导入版本缺少 source.apk");
        return version;
    }
}

