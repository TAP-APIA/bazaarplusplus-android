package com.bazaarplusplus.runtime;

import android.content.Context;
import android.content.ContextWrapper;
import android.content.pm.ApplicationInfo;
import android.content.res.AssetManager;
import android.content.res.Configuration;
import android.content.res.Resources;
import java.io.File;
import java.lang.reflect.Method;

final class GameRuntimeContext extends ContextWrapper {
    private final ClassLoader loader;
    private final Resources resources;
    private final ApplicationInfo applicationInfo;

    GameRuntimeContext(Context base, GameVersion version, ClassLoader loader) throws Exception {
        super(base);
        this.loader = loader;
        AssetManager assets = AssetManager.class.getDeclaredConstructor().newInstance();
        Method addAssetPath = AssetManager.class.getMethod("addAssetPath", String.class);
        int cookie = ((Integer)addAssetPath.invoke(assets, version.apk.getAbsolutePath())).intValue();
        if (cookie == 0) throw new Exception("AssetManager.addAssetPath 返回 0");
        Resources baseResources = base.getResources();
        this.resources = new Resources(assets, baseResources.getDisplayMetrics(), new Configuration(baseResources.getConfiguration()));
        this.applicationInfo = new ApplicationInfo(base.getApplicationInfo());
        this.applicationInfo.packageName = ApkImporter.EXPECTED_PACKAGE;
        this.applicationInfo.sourceDir = version.apk.getAbsolutePath();
        this.applicationInfo.publicSourceDir = version.apk.getAbsolutePath();
        this.applicationInfo.nativeLibraryDir = version.nativeDir.getAbsolutePath();
        this.applicationInfo.dataDir = base.getApplicationInfo().dataDir;
    }

    @Override public ClassLoader getClassLoader() { return loader; }
    @Override public Resources getResources() { return resources; }
    @Override public AssetManager getAssets() { return resources.getAssets(); }
    @Override public String getPackageName() { return ApkImporter.EXPECTED_PACKAGE; }
    @Override public ApplicationInfo getApplicationInfo() { return applicationInfo; }
    @Override public File getFilesDir() { return getBaseContext().getFilesDir(); }
}

