package com.bepinex.android.bpp;

import android.app.Activity;
import android.app.ActivityManager;
import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.pm.Signature;
import android.content.pm.SigningInfo;
import android.content.res.AssetFileDescriptor;
import android.content.res.Configuration;
import android.database.Cursor;
import android.graphics.Point;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.os.StatFs;
import android.provider.Settings;
import android.system.Os;
import android.system.OsConstants;
import android.util.DisplayMetrics;
import android.view.Display;
import android.view.DisplayCutout;
import android.view.View;
import android.view.WindowInsets;
import android.view.WindowManager;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.RandomAccessFile;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.time.Instant;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/**
 * Collects everything the app sandbox can see into one zip, then copies it to Downloads.
 * Every probe is fail-soft: a denied path is written to manifest.txt and the launcher keeps running.
 * No extra manifest permission is requested. logcat without READ_LOGS returns this UID only,
 * and Downloads uses the MediaStore inserts available since Android 10.
 */
public final class BppDiagnosticExporter {

    public static final String FILE_PROVIDER_AUTHORITY = "com.bepinex.android.provider";
    private static final String[] EXPECTED_RELATIVE = new String[] {
        "BepInEx/LogOutput.log",
        "BepInEx/ErrorLog.log",
        "BepInEx/preloader.log",
        "BepInEx/bpp-startup-trace.log",
        "BepInEx/config/BepInEx.cfg",
        "BepInEx/config/BazaarPlusPlus.cfg",
        "modpacks/bpp/modpack.json",
        "modpacks/bpp/config/BazaarPlusPlus.cfg",
        "modpacks/bpp/logs",
        "BepInEx/logs",
        "BepInEx/interop",
        "BepInEx/unity-libs",
        "BepInEx/dummy",
        "PersistentData",
        "Player.log"
    };
    private static final long NATIVE_HASH_CAP = 256L * 1024L * 1024L;
    private static final long DLL_HASH_CAP = 32L * 1024L * 1024L;

    private BppDiagnosticExporter() {}

    public static final class Saved {
        public final boolean ok;
        public final Uri shareUri;
        public final String displayPath;
        public final String sha256;
        public final long sizeBytes;
        public final String warning;
        public final String error;

        Saved(boolean ok, Uri shareUri, String displayPath, String sha256, long sizeBytes, String warning, String error) {
            this.ok = ok;
            this.shareUri = shareUri;
            this.displayPath = displayPath;
            this.sha256 = sha256 == null ? "" : sha256;
            this.sizeBytes = sizeBytes;
            this.warning = warning;
            this.error = error;
        }

        static Saved failure(Throwable error) {
            String message = error == null ? "unknown" : error.getClass().getSimpleName() + ": " + error.getMessage();
            return new Saved(false, null, "", "", 0L, null, message);
        }
    }

    /** Safe to call on the UI thread. Does not touch the game window. */
    public static String captureDisplaySnapshot(Activity activity) {
        StringBuilder sb = new StringBuilder();
        if (activity == null) return "activity=null\n";
        try {
            DisplayMetrics dm = activity.getResources().getDisplayMetrics();
            sb.append("widthPixels=").append(dm.widthPixels)
                .append(" heightPixels=").append(dm.heightPixels)
                .append(" density=").append(dm.density)
                .append(" densityDpi=").append(dm.densityDpi)
                .append(" xdpi=").append(dm.xdpi)
                .append(" ydpi=").append(dm.ydpi)
                .append(" scaledDensity=").append(dm.scaledDensity)
                .append('\n');
            WindowManager wm = activity.getWindowManager();
            Display display = wm.getDefaultDisplay();
            Point real = new Point();
            display.getRealSize(real);
            Point app = new Point();
            display.getSize(app);
            sb.append("realSize=").append(real.x).append('x').append(real.y)
                .append(" appSize=").append(app.x).append('x').append(app.y)
                .append(" rotation=").append(display.getRotation())
                .append(" refreshRate=").append(display.getRefreshRate())
                .append('\n');
            Display.Mode mode = display.getMode();
            sb.append("currentMode id=").append(mode.getModeId())
                .append(" ").append(mode.getPhysicalWidth()).append('x').append(mode.getPhysicalHeight())
                .append(" @").append(mode.getRefreshRate()).append('\n');
            Display.Mode[] modes = display.getSupportedModes();
            if (modes != null) {
                for (int i = 0; i < modes.length && i < 16; i++) {
                    Display.Mode item = modes[i];
                    sb.append("supportedMode id=").append(item.getModeId())
                        .append(" ").append(item.getPhysicalWidth()).append('x').append(item.getPhysicalHeight())
                        .append(" @").append(item.getRefreshRate()).append('\n');
                }
            }
            Configuration config = activity.getResources().getConfiguration();
            sb.append("orientation=").append(config.orientation)
                .append(" screenWidthDp=").append(config.screenWidthDp)
                .append(" screenHeightDp=").append(config.screenHeightDp)
                .append(" smallestScreenWidthDp=").append(config.smallestScreenWidthDp)
                .append(" fontScale=").append(config.fontScale)
                .append(" uiMode=").append(config.uiMode)
                .append(" screenLayout=").append(config.screenLayout)
                .append(" locales=").append(config.getLocales())
                .append('\n');
            View decor = activity.getWindow() == null ? null : activity.getWindow().getDecorView();
            if (decor != null) {
                WindowInsets insets = decor.getRootWindowInsets();
                if (insets != null) {
                    sb.append("systemWindowInsets=")
                        .append(insets.getSystemWindowInsetLeft()).append(',')
                        .append(insets.getSystemWindowInsetTop()).append(',')
                        .append(insets.getSystemWindowInsetRight()).append(',')
                        .append(insets.getSystemWindowInsetBottom()).append('\n');
                    DisplayCutout cutout = insets.getDisplayCutout();
                    if (cutout == null) {
                        sb.append("displayCutout=none\n");
                    } else {
                        sb.append("cutoutSafe=")
                            .append(cutout.getSafeInsetLeft()).append(',')
                            .append(cutout.getSafeInsetTop()).append(',')
                            .append(cutout.getSafeInsetRight()).append(',')
                            .append(cutout.getSafeInsetBottom())
                            .append(" rects=").append(cutout.getBoundingRects())
                            .append('\n');
                    }
                } else {
                    sb.append("rootWindowInsets=null\n");
                }
            }
            WindowManager.LayoutParams attrs = activity.getWindow() == null
                ? null : activity.getWindow().getAttributes();
            if (attrs != null) {
                sb.append("layoutInDisplayCutoutMode=").append(attrs.layoutInDisplayCutoutMode).append('\n');
            }
        } catch (Throwable error) {
            sb.append("displayError=").append(error).append('\n');
        }
        return sb.toString();
    }

    public static Saved exportAndSave(Activity activity, File gameRoot, File internalWorkDir, String displaySnapshot) {
        try {
            return exportUnchecked(activity, gameRoot, internalWorkDir, displaySnapshot);
        } catch (Throwable error) {
            return Saved.failure(error);
        }
    }

    private static Saved exportUnchecked(
        Activity activity,
        File gameRoot,
        File internalWorkDir,
        String displaySnapshot
    ) throws Exception {
        if (activity == null) throw new IllegalStateException("missing activity");
        List<String> errors = new ArrayList<String>();
        List<BppDiagnosticBundle.TextEntry> texts = new ArrayList<BppDiagnosticBundle.TextEntry>();
        List<BppDiagnosticBundle.ScanRoot> scans = new ArrayList<BppDiagnosticBundle.ScanRoot>();
        List<BppDiagnosticBundle.ExtraFile> extras = new ArrayList<BppDiagnosticBundle.ExtraFile>();
        File stage = new File(activity.getCacheDir(), "bpp-diag-stage");
        deleteRecursively(stage);
        if (!stage.isDirectory() && !stage.mkdirs()) {
            throw new IllegalStateException("cannot create " + stage.getAbsolutePath());
        }

        texts.add(new BppDiagnosticBundle.TextEntry("device/build.txt", safeSection(errors, "device", new Section() {
            @Override public String run() { return deviceBuild(activity); }
        })));
        texts.add(new BppDiagnosticBundle.TextEntry("device/display.txt",
            displaySnapshot == null ? "displaySnapshot=missing\n" : displaySnapshot));
        texts.add(new BppDiagnosticBundle.TextEntry("device/memory-storage.txt", safeSection(errors, "memory", new Section() {
            @Override public String run() { return memoryAndStorage(activity); }
        })));
        texts.add(new BppDiagnosticBundle.TextEntry("device/getprop.txt", safeSection(errors, "getprop", new Section() {
            @Override public String run() { return redactProps(capture("getprop", 8000L, 512 * 1024)); }
        })));
        texts.add(new BppDiagnosticBundle.TextEntry("device/settings.txt", safeSection(errors, "settings", new Section() {
            @Override public String run() { return settings(activity); }
        })));
        texts.add(new BppDiagnosticBundle.TextEntry("packages/launcher.txt", safeSection(errors, "launcher-package", new Section() {
            @Override public String run() { return describePackage(activity, activity.getPackageName()); }
        })));
        texts.add(new BppDiagnosticBundle.TextEntry("packages/game.txt", safeSection(errors, "game-package", new Section() {
            @Override public String run() { return describePackage(activity, BppBundledModpackInstaller.TARGET_PACKAGE); }
        })));
        texts.add(new BppDiagnosticBundle.TextEntry("packages/libil2cpp.txt", safeSection(errors, "libil2cpp", new Section() {
            @Override public String run() {
                return nativeLibraries(activity, BppBundledModpackInstaller.TARGET_PACKAGE);
            }
        })));
        texts.add(new BppDiagnosticBundle.TextEntry("deploy/status.txt", safeSection(errors, "deploy", new Section() {
            @Override public String run() { return deployStatus(activity, gameRoot, internalWorkDir); }
        })));
        texts.add(new BppDiagnosticBundle.TextEntry("deploy/file-tree-game-root.txt", safeSection(errors, "tree-game", new Section() {
            @Override public String run() { return BppDiagnosticBundle.renderTree(gameRoot, BppDiagnosticBundle.MAX_TREE_LINES, 6); }
        })));
        texts.add(new BppDiagnosticBundle.TextEntry("deploy/file-tree-internal-work.txt", safeSection(errors, "tree-work", new Section() {
            @Override public String run() { return BppDiagnosticBundle.renderTree(internalWorkDir, 400, 4); }
        })));
        texts.add(new BppDiagnosticBundle.TextEntry("logs/expected-paths.txt", safeSection(errors, "expected-logs", new Section() {
            @Override public String run() { return expectedPaths(activity, gameRoot); }
        })));
        texts.add(new BppDiagnosticBundle.TextEntry("logs/directories.txt", safeSection(errors, "log-dirs", new Section() {
            @Override public String run() { return logDirectories(activity, gameRoot); }
        })));
        texts.add(new BppDiagnosticBundle.TextEntry("state/datastore/keys.txt", safeSection(errors, "datastore-keys", new Section() {
            @Override public String run() {
                return datastoreKeys(new File(activity.getDataDir(), "files/datastore"));
            }
        })));
        texts.add(new BppDiagnosticBundle.TextEntry("crashes/tombstone-probes.txt", safeSection(errors, "tombstones", new Section() {
            @Override public String run() { return tombstoneProbes(activity, gameRoot); }
        })));
        texts.add(new BppDiagnosticBundle.TextEntry("crashes/application-exit.txt", safeSection(errors, "exit-info", new Section() {
            @Override public String run() { return exitReasons(activity, stage, extras); }
        })));
        texts.add(new BppDiagnosticBundle.TextEntry("logcat/dump-notes.txt", safeSection(errors, "logcat", new Section() {
            @Override public String run() { return dumpLogcat(stage, extras); }
        })));
        texts.add(new BppDiagnosticBundle.TextEntry("device/shell-probes.txt", safeSection(errors, "shell", new Section() {
            @Override public String run() { return shellProbes(activity); }
        })));

        addScan(scans, gameRoot, "collected/game-root", false);
        addScan(scans, internalWorkDir, "deploy/internal-work", true);
        File diagnostics = new File(activity.getFilesDir(), BppDiagnosticSessionLog.DIR_NAME);
        addScan(scans, diagnostics, "launch/diagnostics", true);
        File dataDir = activity.getDataDir();
        addScan(scans, new File(dataDir, "shared_prefs"), "state/shared-prefs", true);
        addScan(scans, new File(dataDir, "files/datastore"), "state/datastore", true);
        addScan(scans, new File(dataDir, "no_backup"), "state/no-backup", true);
        File external = activity.getExternalFilesDir(null);
        addScan(scans, external, "collected/external-files", false);
        String gamePackage = BppBundledModpackInstaller.TARGET_PACKAGE;
        addScan(scans, new File("/sdcard/BepInEx_Android/" + gamePackage), "collected/legacy-sdcard", false);
        addScan(scans, new File("/storage/emulated/0/BepInEx_Android/" + gamePackage), "collected/legacy-emulated", false);
        addScan(scans, new File("/sdcard/Android/data/" + activity.getPackageName() + "/files"), "collected/android-data-launcher", false);
        addScan(scans, new File("/storage/emulated/0/Android/data/" + activity.getPackageName() + "/files"), "collected/android-data-launcher-emulated", false);
        addScan(scans, new File("/sdcard/Android/data/" + gamePackage + "/files"), "collected/android-data-game", false);
        addScan(scans, new File("/storage/emulated/0/Android/data/" + gamePackage + "/files"), "collected/android-data-game-emulated", false);
        addReadableTombstones(scans, activity, gameRoot);
        harvestLogs(extras, gameRoot, "logs/game-root");
        harvestLogs(extras, activity.getExternalFilesDir(null), "logs/external-files");
        harvestJitTrace(activity, stage, extras);

        StringBuilder notes = new StringBuilder();
        for (int i = 0; i < errors.size(); i++) notes.append(errors.get(i)).append('\n');
        if (notes.length() == 0) notes.append("(none)\n");
        texts.add(new BppDiagnosticBundle.TextEntry("device/collector-notes.txt", notes.toString()));

        String version = readVersion(activity);
        String stamp = DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss").withLocale(Locale.US)
            .format(java.time.ZonedDateTime.now());
        String zipName = "BPP-diagnostics-" + stamp + ".zip";
        File outDir = new File(activity.getCacheDir(), "bpp-diag-out");
        if (!outDir.isDirectory() && !outDir.mkdirs()) throw new IllegalStateException("cannot create export dir");
        File[] old = outDir.listFiles();
        if (old != null) {
            for (int i = 0; i < old.length; i++) old[i].delete();
        }
        File zip = new File(outDir, zipName);
        String readme = BppDiagnosticBundle.readme(BppDiagnosticBundle.nowIso(), version, activity.getPackageName());
        BppDiagnosticBundle.write(zip, new BppDiagnosticBundle.Request(readme, texts, scans, extras));
        deleteRecursively(stage);
        String hash;
        try {
            hash = BppDiagnosticBundle.sha256Hex(zip, 64L * 1024L * 1024L);
        } catch (Exception ex) {
            hash = "unavailable: " + ex.getMessage();
        }
        return publish(activity, zip, zipName, hash);
    }

    private static Saved publish(Context context, File zip, String zipName, String hash) {
        StringBuilder warning = new StringBuilder();
        Uri share = null;
        String display = zip.getAbsolutePath();
        Uri downloads = insertDownload(context, zip, zipName, "content://media/external/downloads", warning);
        if (downloads == null) {
            downloads = insertDownload(context, zip, zipName, "content://media/external/file", warning);
        }
        if (downloads != null) {
            share = downloads;
            String queried = queryDisplayPath(context.getContentResolver(), downloads, zipName);
            if (queried != null && queried.length() > 0) display = queried;
        } else {
            File direct = tryDirectDownload(zip, zipName, warning);
            if (direct != null) {
                display = direct.getAbsolutePath();
                share = fileProviderUri(context, direct);
                if (share == null) share = Uri.fromFile(direct);
            }
        }
        if (share == null || downloads == null) {
            File externalDir = context.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS);
            File externalCopy = externalDir == null ? null : new File(externalDir, zipName);
            if (externalCopy != null && copyFile(zip, externalCopy, warning)) {
                if (downloads == null) display = externalCopy.getAbsolutePath();
                Uri provider = fileProviderUri(context, externalCopy);
                if (share == null) share = provider;
                warning.append("公共下载目录不可写，已另存到应用外部目录。\n");
            } else {
                Uri provider = fileProviderUri(context, zip);
                if (share == null) share = provider;
                warning.append("未能写入系统「下载」，文件仍在应用缓存。\n");
            }
        }
        if (share == null) warning.append("没有可用的分享 Uri。\n");
        return new Saved(true, share, display, hash, zip.length(), warning.length() == 0 ? null : warning.toString(), null);
    }

    private static Uri insertDownload(Context context, File zip, String zipName, String collection, StringBuilder warning) {
        if (Build.VERSION.SDK_INT < 29 && collection.contains("/downloads")) {
            warning.append("API ").append(Build.VERSION.SDK_INT).append(" 没有 Downloads MediaStore。\n");
            return null;
        }
        ContentResolver resolver = context.getContentResolver();
        ContentValues values = new ContentValues();
        values.put("_display_name", zipName);
        values.put("mime_type", "application/zip");
        if (Build.VERSION.SDK_INT >= 29) {
            values.put("relative_path", Environment.DIRECTORY_DOWNLOADS);
            values.put("is_pending", 1);
        }
        Uri uri = null;
        try {
            uri = resolver.insert(Uri.parse(collection), values);
        } catch (Throwable first) {
            values.remove("is_pending");
            try {
                uri = resolver.insert(Uri.parse(collection), values);
            } catch (Throwable second) {
                warning.append(collection).append(" insert 失败: ").append(second.getClass().getSimpleName())
                    .append(' ').append(second.getMessage()).append('\n');
                return null;
            }
        }
        if (uri == null) {
            warning.append(collection).append(" insert 返回 null。\n");
            return null;
        }
        try (InputStream in = new FileInputStream(zip);
             java.io.OutputStream out = resolver.openOutputStream(uri)) {
            if (out == null) throw new java.io.IOException("openOutputStream returned null");
            byte[] buf = new byte[65536];
            int n;
            while ((n = in.read(buf)) != -1) out.write(buf, 0, n);
            out.flush();
            if (out instanceof FileOutputStream) ((FileOutputStream) out).getFD().sync();
        } catch (Throwable error) {
            try {
                resolver.delete(uri, null, null);
            } catch (Throwable ignored) {
            }
            warning.append(collection).append(" 写入失败: ").append(error.getMessage()).append('\n');
            return null;
        }
        if (Build.VERSION.SDK_INT >= 29) {
            try {
                ContentValues done = new ContentValues();
                done.put("is_pending", 0);
                resolver.update(uri, done, null, null);
            } catch (Throwable ignored) {
            }
        }
        return uri;
    }

    private static File tryDirectDownload(File zip, String zipName, StringBuilder warning) {
        try {
            File dir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS);
            if (dir == null) return null;
            if (!dir.isDirectory() && !dir.mkdirs()) return null;
            File dest = new File(dir, zipName);
            if (!copyFile(zip, dest, warning)) return null;
            return dest;
        } catch (Throwable error) {
            warning.append("直接写入 Download 失败: ").append(error.getMessage()).append('\n');
            return null;
        }
    }

    private static boolean copyFile(File from, File to, StringBuilder warning) {
        File parent = to.getParentFile();
        if (parent != null && !parent.isDirectory() && !parent.mkdirs()) return false;
        try (InputStream in = new FileInputStream(from); FileOutputStream out = new FileOutputStream(to)) {
            byte[] buf = new byte[65536];
            int n;
            while ((n = in.read(buf)) != -1) out.write(buf, 0, n);
            out.getFD().sync();
            return true;
        } catch (Throwable error) {
            warning.append(to.getAbsolutePath()).append(" 复制失败: ").append(error.getMessage()).append('\n');
            return false;
        }
    }

    private static Uri fileProviderUri(Context context, File file) {
        if (file == null || !file.isFile()) return null;
        try {
            Class<?> type = Class.forName("androidx.core.content.FileProvider");
            Method method = type.getMethod("getUriForFile", Context.class, String.class, File.class);
            Object uri = method.invoke(null, context, FILE_PROVIDER_AUTHORITY, file);
            if (uri instanceof Uri) return (Uri) uri;
        } catch (Throwable ignored) {
        }
        return null;
    }

    private static String queryDisplayPath(ContentResolver resolver, Uri uri, String fallbackName) {
        String[] projection = new String[] {"_data", "_display_name", "relative_path"};
        Cursor cursor = null;
        try {
            cursor = resolver.query(uri, projection, null, null, null);
            if (cursor != null && cursor.moveToFirst()) {
                String data = cursor.getColumnIndex("_data") >= 0 && !cursor.isNull(0) ? cursor.getString(0) : null;
                String name = cursor.getColumnCount() > 1 && !cursor.isNull(1) ? cursor.getString(1) : fallbackName;
                String relative = cursor.getColumnCount() > 2 && !cursor.isNull(2) ? cursor.getString(2) : null;
                if (data != null && data.length() > 0) return data;
                if (relative != null && relative.length() > 0) return relative + name;
            }
        } catch (Throwable ignored) {
        } finally {
            if (cursor != null) cursor.close();
        }
        return Environment.DIRECTORY_DOWNLOADS + "/" + fallbackName;
    }

    private static String deviceBuild(Context context) {
        StringBuilder sb = new StringBuilder();
        sb.append("manufacturer=").append(Build.MANUFACTURER).append('\n');
        sb.append("brand=").append(Build.BRAND).append('\n');
        sb.append("model=").append(Build.MODEL).append('\n');
        sb.append("device=").append(Build.DEVICE).append('\n');
        sb.append("product=").append(Build.PRODUCT).append('\n');
        sb.append("hardware=").append(Build.HARDWARE).append('\n');
        sb.append("board=").append(Build.BOARD).append('\n');
        sb.append("bootloader=").append(Build.BOOTLOADER).append('\n');
        sb.append("display=").append(Build.DISPLAY).append('\n');
        sb.append("fingerprint=").append(Build.FINGERPRINT).append('\n');
        sb.append("host=").append(Build.HOST).append('\n');
        sb.append("id=").append(Build.ID).append('\n');
        sb.append("tags=").append(Build.TAGS).append('\n');
        sb.append("type=").append(Build.TYPE).append('\n');
        sb.append("user=").append(Build.USER).append('\n');
        sb.append("buildTime=").append(formatTime(Build.TIME)).append('\n');
        sb.append("release=").append(Build.VERSION.RELEASE).append('\n');
        sb.append("sdkInt=").append(Build.VERSION.SDK_INT).append('\n');
        sb.append("codename=").append(Build.VERSION.CODENAME).append('\n');
        sb.append("incremental=").append(Build.VERSION.INCREMENTAL).append('\n');
        sb.append("securityPatch=").append(Build.VERSION.SECURITY_PATCH).append('\n');
        sb.append("baseOs=").append(Build.VERSION.BASE_OS).append('\n');
        sb.append("previewSdkInt=").append(Build.VERSION.PREVIEW_SDK_INT).append('\n');
        sb.append("abis=").append(Arrays.toString(Build.SUPPORTED_ABIS)).append('\n');
        sb.append("abis32=").append(Arrays.toString(Build.SUPPORTED_32_BIT_ABIS)).append('\n');
        sb.append("abis64=").append(Arrays.toString(Build.SUPPORTED_64_BIT_ABIS)).append('\n');
        try {
            sb.append("pageSize=").append(Os.sysconf(OsConstants._SC_PAGESIZE)).append('\n');
        } catch (Throwable error) {
            sb.append("pageSize=unavailable ").append(error).append('\n');
        }
        appendReflected(sb, Build.VERSION.class, "MEDIA_PERFORMANCE_CLASS", "mediaPerformanceClass");
        appendReflected(sb, Build.VERSION.class, "RELEASE_OR_CODENAME", "releaseOrCodename");
        sb.append("timezone=").append(ZoneId.systemDefault()).append('\n');
        sb.append("locale=").append(Locale.getDefault()).append('\n');
        sb.append("now=").append(BppDiagnosticBundle.nowIso()).append('\n');
        sb.append("launcherPackage=").append(context.getPackageName()).append('\n');
        sb.append("myPid=").append(android.os.Process.myPid()).append('\n');
        sb.append("myUid=").append(android.os.Process.myUid()).append('\n');
        return sb.toString();
    }

    private static String memoryAndStorage(Context context) {
        StringBuilder sb = new StringBuilder();
        Runtime runtime = Runtime.getRuntime();
        sb.append("runtimeMax=").append(runtime.maxMemory())
            .append(" total=").append(runtime.totalMemory())
            .append(" free=").append(runtime.freeMemory()).append('\n');
        try {
            ActivityManager am = (ActivityManager) context.getSystemService(Context.ACTIVITY_SERVICE);
            if (am != null) {
                ActivityManager.MemoryInfo info = new ActivityManager.MemoryInfo();
                am.getMemoryInfo(info);
                sb.append("memAvail=").append(info.availMem)
                    .append(" memTotal=").append(info.totalMem)
                    .append(" threshold=").append(info.threshold)
                    .append(" lowMemory=").append(info.lowMemory)
                    .append(" lowRamDevice=").append(am.isLowRamDevice())
                    .append('\n');
            }
        } catch (Throwable error) {
            sb.append("memoryInfo=unavailable ").append(error).append('\n');
        }
        appendStat(sb, "data", Environment.getDataDirectory());
        appendStat(sb, "external", Environment.getExternalStorageDirectory());
        File files = context.getFilesDir();
        if (files != null) appendStat(sb, "files", files);
        sb.append("\n[/proc/meminfo]\n").append(readHead(new File("/proc/meminfo"), 8192));
        sb.append("\n[/proc/cpuinfo]\n").append(readHead(new File("/proc/cpuinfo"), 16384));
        sb.append("\n[/proc/self/status]\n").append(readHead(new File("/proc/self/status"), 8192));
        return sb.toString();
    }

    private static String settings(Context context) {
        StringBuilder sb = new StringBuilder();
        ContentResolver resolver = context.getContentResolver();
        appendSetting(sb, resolver, Settings.Global.ANIMATOR_DURATION_SCALE);
        appendSetting(sb, resolver, Settings.Global.TRANSITION_ANIMATION_SCALE);
        appendSetting(sb, resolver, Settings.Global.WINDOW_ANIMATION_SCALE);
        appendSetting(sb, resolver, Settings.Global.DEVELOPMENT_SETTINGS_ENABLED);
        try {
            sb.append("androidId=").append(Settings.Secure.getString(resolver, Settings.Secure.ANDROID_ID)).append('\n');
        } catch (Throwable error) {
            sb.append("androidId=unavailable ").append(error.getClass().getSimpleName()).append('\n');
        }
        return sb.toString();
    }

    private static String describePackage(Context context, String packageName) {
        StringBuilder sb = new StringBuilder();
        sb.append("package=").append(packageName).append('\n');
        PackageManager pm = context.getPackageManager();
        PackageInfo info = null;
        try {
            info = pm.getPackageInfo(packageName, PackageManager.GET_SIGNING_CERTIFICATES);
        } catch (Throwable first) {
            sb.append("getPackageInfoWithCerts=").append(first).append('\n');
            try {
                info = pm.getPackageInfo(packageName, 0);
            } catch (Throwable second) {
                sb.append("getPackageInfo=unavailable ").append(second).append('\n');
                return sb.toString();
            }
        }
        sb.append("versionName=").append(info.versionName).append('\n');
        sb.append("versionCode=").append(info.getLongVersionCode()).append('\n');
        sb.append("firstInstallTime=").append(formatTime(info.firstInstallTime)).append('\n');
        sb.append("lastUpdateTime=").append(formatTime(info.lastUpdateTime)).append('\n');
        try {
            sb.append("installer=").append(pm.getInstallerPackageName(packageName)).append('\n');
        } catch (Throwable error) {
            sb.append("installer=unavailable ").append(error).append('\n');
        }
        try {
            android.content.Intent launch = pm.getLaunchIntentForPackage(packageName);
            sb.append("launchIntent=").append(launch == null ? "null" : launch.toString()).append('\n');
        } catch (Throwable error) {
            sb.append("launchIntent=unavailable ").append(error).append('\n');
        }
        ApplicationInfo app = info.applicationInfo;
        if (app != null) {
            sb.append("sourceDir=").append(app.sourceDir).append('\n');
            sb.append("publicSourceDir=").append(app.publicSourceDir).append('\n');
            sb.append("nativeLibraryDir=").append(app.nativeLibraryDir).append('\n');
            sb.append("dataDir=").append(app.dataDir).append('\n');
            sb.append("targetSdk=").append(app.targetSdkVersion).append('\n');
            sb.append("minSdk=").append(app.minSdkVersion).append('\n');
            sb.append("uid=").append(app.uid).append('\n');
            sb.append("flags=0x").append(Integer.toHexString(app.flags)).append('\n');
            sb.append("extractNativeLibs=").append((app.flags & ApplicationInfo.FLAG_EXTRACT_NATIVE_LIBS) != 0).append('\n');
            appendFileStat(sb, "baseApk", app.sourceDir == null ? null : new File(app.sourceDir));
            if (app.splitSourceDirs != null) {
                for (int i = 0; i < app.splitSourceDirs.length; i++) {
                    String splitName = info.splitNames != null && i < info.splitNames.length ? info.splitNames[i] : ("split" + i);
                    sb.append("splitName=").append(splitName).append('\n');
                    appendFileStat(sb, "splitApk", new File(app.splitSourceDirs[i]));
                }
            } else {
                sb.append("splitSourceDirs=none\n");
            }
        }
        appendSignatures(sb, info);
        return sb.toString();
    }

    private static void appendSignatures(StringBuilder sb, PackageInfo info) {
        try {
            SigningInfo signing = info.signingInfo;
            if (signing == null) {
                sb.append("signingInfo=null\n");
                return;
            }
            sb.append("hasMultipleSigners=").append(signing.hasMultipleSigners()).append('\n');
            sb.append("hasPastSigningCertificates=").append(signing.hasPastSigningCertificates()).append('\n');
            appendCerts(sb, "apkContentsSigner", signing.getApkContentsSigners());
            if (signing.hasPastSigningCertificates()) {
                appendCerts(sb, "signingCertificateHistory", signing.getSigningCertificateHistory());
            }
        } catch (Throwable error) {
            sb.append("signingInfo=unavailable ").append(error).append('\n');
        }
    }

    private static void appendCerts(StringBuilder sb, String label, Signature[] signatures) {
        if (signatures == null) {
            sb.append(label).append("=null\n");
            return;
        }
        for (int i = 0; i < signatures.length; i++) {
            try {
                byte[] encoded = signatures[i].toByteArray();
                sb.append(label).append('[').append(i).append("] sha256=").append(sha256(encoded))
                    .append(" bytes=").append(encoded.length).append('\n');
            } catch (Throwable error) {
                sb.append(label).append('[').append(i).append("]=unavailable ").append(error).append('\n');
            }
        }
    }

    private static String nativeLibraries(Context context, String gamePackage) {
        StringBuilder sb = new StringBuilder();
        long page = -1L;
        try {
            page = Os.sysconf(OsConstants._SC_PAGESIZE);
        } catch (Throwable ignored) {
        }
        sb.append("devicePageSize=").append(page).append('\n');
        List<File> apks = new ArrayList<File>();
        describeNativeDir(sb, "launcherNative", nativeDir(context, context.getPackageName()), apks);
        describeNativeDir(sb, "gameNative", nativeDir(context, gamePackage), apks);
        PackageInfo game = packageOrNull(context, gamePackage);
        if (game != null && game.applicationInfo != null) {
            addApk(apks, game.applicationInfo.sourceDir);
            if (game.applicationInfo.splitSourceDirs != null) {
                for (int i = 0; i < game.applicationInfo.splitSourceDirs.length; i++) {
                    addApk(apks, game.applicationInfo.splitSourceDirs[i]);
                }
            }
        }
        PackageInfo self = packageOrNull(context, context.getPackageName());
        if (self != null && self.applicationInfo != null) addApk(apks, self.applicationInfo.sourceDir);
        for (int i = 0; i < apks.size(); i++) {
            probeApkNative(sb, apks.get(i), page);
        }
        return sb.toString();
    }

    private static void describeNativeDir(StringBuilder sb, String label, File dir, List<File> apks) {
        sb.append('[').append(label).append("] path=").append(dir == null ? "null" : dir.getAbsolutePath()).append('\n');
        if (dir == null || !dir.exists()) {
            sb.append("exists=false\n");
            return;
        }
        if (!dir.canRead()) {
            sb.append("readable=false\n");
            return;
        }
        File[] files = dir.listFiles();
        if (files == null) {
            sb.append("list=null\n");
            return;
        }
        Arrays.sort(files);
        for (int i = 0; i < files.length && i < 80; i++) {
            File file = files[i];
            sb.append(file.getName()).append(" size=").append(file.length()).append(" readable=").append(file.canRead()).append('\n');
            if ("libil2cpp.so".equals(file.getName()) || "libunity.so".equals(file.getName())
                || "libmain.so".equals(file.getName()) || "libpine.so".equals(file.getName())) {
                appendElf(sb, file, file.length(), null);
            }
        }
    }

    private static void probeApkNative(StringBuilder sb, File apk, long page) {
        sb.append("[apk] ").append(apk.getAbsolutePath()).append(" size=").append(apk.length())
            .append(" readable=").append(apk.canRead()).append('\n');
        if (!apk.isFile() || !apk.canRead()) return;
        ZipFile zip = null;
        try {
            zip = new ZipFile(apk);
            String[] names = new String[] {
                "lib/arm64-v8a/libil2cpp.so",
                "lib/arm64-v8a/libunity.so",
                "lib/arm64-v8a/libmain.so",
                "lib/arm64-v8a/libpine.so"
            };
            for (int i = 0; i < names.length; i++) {
                ZipEntry entry = zip.getEntry(names[i]);
                if (entry == null) {
                    sb.append(names[i]).append("=absent\n");
                    continue;
                }
                sb.append(names[i]).append(" uncompressed=").append(entry.getSize())
                    .append(" compressed=").append(entry.getCompressedSize()).append('\n');
                if ("lib/arm64-v8a/libil2cpp.so".equals(names[i])) {
                    hashZipEntry(sb, zip, entry);
                    byte[] prefix = readZipPrefix(zip, entry, 64 * 1024);
                    sb.append(BppDiagnosticBundle.describeElfLoadSegments(prefix, apk.getName() + "!" + names[i]));
                    appendAlignComparison(sb, prefix, page);
                }
            }
        } catch (Throwable error) {
            sb.append("zipError=").append(error).append('\n');
        } finally {
            if (zip != null) {
                try {
                    zip.close();
                } catch (Throwable ignored) {
                }
            }
        }
    }

    private static void appendElf(StringBuilder sb, File file, long size, Long page) {
        try {
            if (size <= NATIVE_HASH_CAP && "libil2cpp.so".equals(file.getName())) {
                sb.append("sha256=").append(BppDiagnosticBundle.sha256Hex(file, NATIVE_HASH_CAP)).append('\n');
            }
            byte[] prefix = BppDiagnosticBundle.readPrefix(file, 64 * 1024);
            sb.append(BppDiagnosticBundle.describeElfLoadSegments(prefix, file.getAbsolutePath()));
            long pageSize = page == null ? Os.sysconf(OsConstants._SC_PAGESIZE) : page.longValue();
            appendAlignComparison(sb, prefix, pageSize);
        } catch (Throwable error) {
            sb.append("elfProbe=unavailable ").append(error).append('\n');
        }
    }

    private static void appendAlignComparison(StringBuilder sb, byte[] prefix, long page) {
        String described = BppDiagnosticBundle.describeElfLoadSegments(prefix, "compare");
        int marker = described.indexOf("minLoadAlign=");
        if (marker < 0 || page <= 0) {
            sb.append("minLoadAlignBelowDevicePage=unknown\n");
            return;
        }
        int end = described.indexOf('\n', marker);
        String number = described.substring(marker + "minLoadAlign=".length(), end < 0 ? described.length() : end).trim();
        try {
            long align = Long.parseLong(number);
            sb.append("minLoadAlignBelowDevicePage=").append(align < page).append('\n');
        } catch (NumberFormatException ex) {
            sb.append("minLoadAlignBelowDevicePage=unknown\n");
        }
    }

    private static void hashZipEntry(StringBuilder sb, ZipFile zip, ZipEntry entry) {
        try (InputStream in = zip.getInputStream(entry)) {
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            byte[] buf = new byte[65536];
            long total = 0L;
            int n;
            while ((n = in.read(buf)) != -1) {
                total += n;
                if (total > NATIVE_HASH_CAP) {
                    sb.append("sha256=skipped over cap\n");
                    return;
                }
                digest.update(buf, 0, n);
            }
            sb.append("sha256=").append(hex(digest.digest())).append(" hashedBytes=").append(total).append('\n');
        } catch (Throwable error) {
            sb.append("sha256=unavailable ").append(error).append('\n');
        }
    }

    private static byte[] readZipPrefix(ZipFile zip, ZipEntry entry, int max) {
        try (InputStream in = zip.getInputStream(entry)) {
            byte[] data = new byte[max];
            int off = 0;
            while (off < data.length) {
                int n = in.read(data, off, data.length - off);
                if (n < 0) break;
                off += n;
            }
            if (off == data.length) return data;
            byte[] trimmed = new byte[off];
            System.arraycopy(data, 0, trimmed, 0, off);
            return trimmed;
        } catch (Throwable error) {
            return new byte[0];
        }
    }

    private static String deployStatus(Activity activity, File gameRoot, File internalWorkDir) {
        StringBuilder sb = new StringBuilder();
        sb.append("gameRoot=").append(gameRoot == null ? "null" : gameRoot.getAbsolutePath()).append('\n');
        sb.append("internalWork=").append(internalWorkDir == null ? "null" : internalWorkDir.getAbsolutePath()).append('\n');
        File state = internalWorkDir == null ? null : new File(internalWorkDir, BppBundledModpackInstaller.STATE_FILE_NAME);
        appendFileStat(sb, "stateFile", state);
        if (state != null && state.isFile() && state.canRead() && state.length() < 1024 * 1024) {
            sb.append("stateBytes:\n").append(readHead(state, (int) state.length())).append('\n');
        }
        hashIfPresent(sb, "runtimeDll", gameRoot == null ? null : new File(gameRoot, "BepInEx/plugins/BazaarPlusPlus.dll"));
        hashIfPresent(sb, "modpackDll", gameRoot == null ? null : new File(gameRoot, "modpacks/bpp/plugins/BazaarPlusPlus.dll"));
        appendTreeSummary(sb, "interop", gameRoot == null ? null : new File(gameRoot, "BepInEx/interop"), ".dll");
        appendTreeSummary(sb, "unityLibs", gameRoot == null ? null : new File(gameRoot, "BepInEx/unity-libs"), null);
        appendTreeSummary(sb, "dummy", gameRoot == null ? null : new File(gameRoot, "BepInEx/dummy"), null);
        appendTreeSummary(sb, "bepInExLogs", gameRoot == null ? null : new File(gameRoot, "BepInEx/logs"), null);
        appendTreeSummary(sb, "modpackLogs", gameRoot == null ? null : new File(gameRoot, "modpacks/bpp/logs"), null);
        try (InputStream in = activity.getAssets().open(BppBundledModpackInstaller.ASSET_MODPACK_MANIFEST)) {
            BppBundledModpackInstaller.Manifest manifest = BppBundledModpackInstaller.parseManifest(in);
            sb.append("bundledBundle=").append(manifest.bundleId)
                .append(" version=").append(manifest.version)
                .append(" files=").append(manifest.entries.size())
                .append(" managedDlls=").append(manifest.managedDllCount).append('\n');
            if (gameRoot != null && internalWorkDir != null) {
                BppBundledModpackInstaller.StatusReport report = BppBundledModpackInstaller.inspectStatus(
                    gameRoot, internalWorkDir, manifest, true);
                sb.append("status=").append(report.kind)
                    .append(" detail=").append(report.detail)
                    .append(" bundle=").append(report.bundleId)
                    .append(" version=").append(report.version).append('\n');
            }
        } catch (Throwable error) {
            sb.append("bundledManifest=unavailable ").append(error).append('\n');
        }
        try (InputStream in = activity.getAssets().open(BppBundledModpackInstaller.ASSET_MODPACK_ZIP)) {
            byte[] buf = new byte[8192];
            long total = 0L;
            int n;
            while ((n = in.read(buf)) != -1) total += n;
            sb.append("bundledZipStreamLength=").append(total).append('\n');
        } catch (Throwable error) {
            sb.append("bundledZipStreamLength=unavailable ").append(error.getClass().getSimpleName())
                .append(' ').append(error.getMessage()).append('\n');
        }
        return sb.toString();
    }

    private static void appendTreeSummary(StringBuilder sb, String label, File dir, String suffix) {
        BppDiagnosticBundle.DirStat stat = BppDiagnosticBundle.summarizeTree(dir, suffix, 3);
        sb.append(label)
            .append(" path=").append(dir == null ? "null" : dir.getAbsolutePath())
            .append(" missing=").append(stat.missing)
            .append(" unreadable=").append(stat.unreadable)
            .append(" files=").append(stat.files)
            .append(" bytes=").append(stat.bytes)
            .append('\n');
    }

    private static String logDirectories(Context context, File gameRoot) {
        StringBuilder sb = new StringBuilder();
        File[] dirs = new File[] {
            gameRoot == null ? null : new File(gameRoot, "BepInEx/logs"),
            gameRoot == null ? null : new File(gameRoot, "modpacks/bpp/logs"),
            gameRoot == null ? null : new File(gameRoot, "BepInEx/interop"),
            gameRoot == null ? null : new File(gameRoot, "BepInEx/unity-libs"),
            gameRoot == null ? null : new File(gameRoot, "BepInEx/dummy"),
            context.getExternalFilesDir(null)
        };
        for (int i = 0; i < dirs.length; i++) {
            sb.append("====\n").append(BppDiagnosticBundle.describeDirectory(dirs[i], 80));
        }
        return sb.toString();
    }

    private static String datastoreKeys(File dir) {
        StringBuilder sb = new StringBuilder();
        if (dir == null || !dir.exists()) return "(missing)\n";
        if (!dir.isDirectory()) return "(not a directory)\n";
        File[] files = dir.listFiles();
        if (files == null) return "(unreadable)\n";
        Arrays.sort(files, new java.util.Comparator<File>() {
            @Override
            public int compare(File a, File b) {
                return a.getName().compareTo(b.getName());
            }
        });
        if (files.length == 0) sb.append("(empty)\n");
        for (int i = 0; i < files.length; i++) {
            File file = files[i];
            String name = file.getName().toLowerCase(java.util.Locale.ROOT);
            if (!name.endsWith(".preferences_pb") && !name.endsWith(".pb")) continue;
            sb.append("## ").append(file.getName()).append(" bytes=").append(file.length()).append('\n');
            try {
                sb.append(BppDiagnosticBundle.decodePreferenceProto(BppDiagnosticBundle.readPrefix(file, 256 * 1024)));
            } catch (Throwable error) {
                sb.append("unavailable ").append(error.getClass().getSimpleName()).append('\n');
            }
        }
        return sb.toString();
    }

    private static void harvestLogs(List<BppDiagnosticBundle.ExtraFile> extras, File root, String prefix) {
        if (root == null || extras == null) return;
        List<File> logs = BppDiagnosticBundle.findLogFiles(root, 200);
        String rootPath = root.getAbsolutePath();
        for (int i = 0; i < logs.size(); i++) {
            File file = logs.get(i);
            String path = file.getAbsolutePath();
            String relative = path.startsWith(rootPath) ? path.substring(rootPath.length()) : file.getName();
            while (relative.startsWith("/")) relative = relative.substring(1);
            relative = relative.replace('\\', '/');
            if (relative.length() == 0) relative = file.getName();
            extras.add(new BppDiagnosticBundle.ExtraFile(file, prefix + "/" + relative, true));
        }
    }

    private static void harvestJitTrace(Activity activity, File stage, List<BppDiagnosticBundle.ExtraFile> extras) {
        File dir = BppJitTrace.traceDir(activity);
        if (dir != null && dir.isDirectory()) {
            File[] files = dir.listFiles();
            if (files != null) {
                Arrays.sort(files, new java.util.Comparator<File>() {
                    @Override
                    public int compare(File a, File b) {
                        return a.getName().compareTo(b.getName());
                    }
                });
                for (int i = 0; i < files.length; i++) {
                    File file = files[i];
                    if (!file.isFile()) continue;
                    String name = file.getName();
                    String lower = name.toLowerCase(Locale.ROOT);
                    if (lower.endsWith(".log") || lower.endsWith(".txt")) {
                        addJitTail(activity, stage, extras, file, "jit/" + name);
                    } else if (lower.endsWith(".nettrace")) {
                        extras.add(new BppDiagnosticBundle.ExtraFile(
                            file, "jit/" + name, false, 24 * 1024 * 1024));
                    }
                }
            }
        }
        File external = activity.getExternalFilesDir(null);
        addJitFusionTail(activity, stage, extras, external,
            "coreclr_stdout.log", "jit/fusion-coreclr_stdout.log");
        addJitFusionTail(activity, stage, extras, external,
            "coreclr_stderr.log", "jit/fusion-coreclr_stderr.log");
    }

    private static void addJitFusionTail(
        Activity activity,
        File stage,
        List<BppDiagnosticBundle.ExtraFile> extras,
        File external,
        String sourceName,
        String archivePath
    ) {
        File source = external == null ? null : new File(external, sourceName);
        addJitTail(activity, stage, extras, source, archivePath);
    }

    private static void addJitTail(
        Activity activity,
        File stage,
        List<BppDiagnosticBundle.ExtraFile> extras,
        File source,
        String archivePath
    ) {
        if (source == null || !source.isFile()) {
            extras.add(new BppDiagnosticBundle.ExtraFile(source, archivePath, true));
            return;
        }
        final int cap = 4 * 1024 * 1024;
        long size;
        try {
            size = source.length();
        } catch (Throwable error) {
            BppGameProcess.logEvent(activity, "jit trace size failed: " + source, error);
            extras.add(new BppDiagnosticBundle.ExtraFile(source, archivePath, true));
            return;
        }
        long keepOffset = Math.max(0L, size - cap);
        long keepLength = Math.min(size, (long) cap);
        boolean truncated = size > cap;
        File staged = new File(stage, archivePath);
        File parent = staged.getParentFile();
        if (parent != null && !parent.isDirectory() && !parent.mkdirs()) {
            BppGameProcess.logEvent(activity, "jit trace stage mkdir failed: " + parent);
            extras.add(new BppDiagnosticBundle.ExtraFile(source, archivePath, true));
            return;
        }
        try (RandomAccessFile raf = new RandomAccessFile(source, "r");
             FileOutputStream out = new FileOutputStream(staged)) {
            if (truncated) {
                String header = "# originalBytes=" + size + " keptTail=" + keepLength + "\n";
                out.write(header.getBytes(StandardCharsets.UTF_8));
            }
            raf.seek(keepOffset);
            byte[] buf = new byte[65536];
            long remaining = keepLength;
            while (remaining > 0L) {
                int want = (int) Math.min((long) buf.length, remaining);
                int n = raf.read(buf, 0, want);
                if (n < 0) break;
                out.write(buf, 0, n);
                remaining -= n;
            }
            out.getFD().sync();
        } catch (Throwable error) {
            BppGameProcess.logEvent(activity, "jit trace tail failed: " + source, error);
            extras.add(new BppDiagnosticBundle.ExtraFile(source, archivePath, true));
            return;
        }
        extras.add(new BppDiagnosticBundle.ExtraFile(staged, archivePath, true));
    }

    private static String expectedPaths(Context context, File gameRoot) {
        StringBuilder sb = new StringBuilder();
        describeExpected(sb, "game-root", gameRoot);
        File external = context.getExternalFilesDir(null);
        describeExpected(sb, "external-files", external);
        describeExpected(sb, "legacy-sdcard", new File("/sdcard/BepInEx_Android/" + BppBundledModpackInstaller.TARGET_PACKAGE));
        describeExpected(sb, "legacy-emulated",
            new File("/storage/emulated/0/BepInEx_Android/" + BppBundledModpackInstaller.TARGET_PACKAGE));
        if (external != null) {
            appendFileStat(sb, "externalPlayerLog", new File(external, "Player.log"));
            appendFileStat(sb, "externalUnityLog", new File(external, "unity.log"));
        }
        return sb.toString();
    }

    private static void describeExpected(StringBuilder sb, String label, File root) {
        sb.append('[').append(label).append("] root=")
            .append(root == null ? "null" : root.getAbsolutePath()).append('\n');
        if (root == null || !root.exists()) {
            sb.append("rootExists=false\n");
            return;
        }
        if (!root.canRead()) {
            sb.append("rootReadable=false\n");
            return;
        }
        for (int i = 0; i < EXPECTED_RELATIVE.length; i++) {
            File file = new File(root, EXPECTED_RELATIVE[i]);
            sb.append(EXPECTED_RELATIVE[i])
                .append(" exists=").append(file.exists())
                .append(" readable=").append(file.canRead())
                .append(" dir=").append(file.isDirectory())
                .append(" size=").append(file.exists() && !file.isDirectory() ? file.length() : -1)
                .append(" mtime=").append(file.exists() ? file.lastModified() : 0)
                .append('\n');
        }
    }

    private static String tombstoneProbes(Context context, File gameRoot) {
        StringBuilder sb = new StringBuilder();
        List<File> probes = new ArrayList<File>();
        probes.add(new File("/data/tombstones"));
        probes.add(new File("/data/anr"));
        probes.add(new File("/data/anr/traces.txt"));
        probes.add(new File("/proc/last_kmsg"));
        probes.add(new File("/sys/fs/pstore"));
        if (gameRoot != null) probes.add(new File(gameRoot, "PersistentData"));
        File external = context.getExternalFilesDir(null);
        if (external != null) probes.add(new File(external, "PersistentData"));
        probes.add(new File("/sdcard/BepInEx_Android/" + BppBundledModpackInstaller.TARGET_PACKAGE + "/PersistentData"));
        probes.add(new File("/storage/emulated/0/BepInEx_Android/" + BppBundledModpackInstaller.TARGET_PACKAGE + "/PersistentData"));
        for (int i = 0; i < probes.size(); i++) {
            File file = probes.get(i);
            sb.append(file.getAbsolutePath())
                .append(" exists=").append(file.exists())
                .append(" readable=").append(file.canRead())
                .append(" dir=").append(file.isDirectory())
                .append(" size=").append(file.isFile() ? file.length() : -1);
            if (file.isDirectory() && file.canRead()) {
                String[] names = file.list();
                sb.append(" children=").append(names == null ? "null" : names.length);
                if (names != null) {
                    Arrays.sort(names);
                    int limit = Math.min(names.length, 30);
                    for (int n = 0; n < limit; n++) sb.append(" name=").append(names[n]);
                }
            }
            sb.append('\n');
        }
        return sb.toString();
    }

    private static void addReadableTombstones(List<BppDiagnosticBundle.ScanRoot> scans, Context context, File gameRoot) {
        File[] dirs = new File[] {
            new File("/data/tombstones"),
            new File("/data/anr"),
            gameRoot == null ? null : new File(gameRoot, "PersistentData")
        };
        for (int i = 0; i < dirs.length; i++) {
            File dir = dirs[i];
            if (dir != null && dir.isDirectory() && dir.canRead()) {
                addScan(scans, dir, "crashes/probe-" + i, false);
            }
        }
    }

    private static String exitReasons(Activity activity, File stage, List<BppDiagnosticBundle.ExtraFile> extras) {
        StringBuilder sb = new StringBuilder();
        if (Build.VERSION.SDK_INT < 30) {
            sb.append("applicationExitInfo=unavailable before API 30 (sdk=").append(Build.VERSION.SDK_INT).append(")\n");
            return sb.toString();
        }
        try {
            ActivityManager am = (ActivityManager) activity.getSystemService(Context.ACTIVITY_SERVICE);
            if (am == null) {
                sb.append("activityManager=null\n");
                return sb.toString();
            }
            Method historical = ActivityManager.class.getMethod(
                "getHistoricalProcessExitReasons", String.class, int.class, int.class);
            Object raw = historical.invoke(am, activity.getPackageName(), Integer.valueOf(0), Integer.valueOf(30));
            if (!(raw instanceof List)) {
                sb.append("resultType=").append(raw == null ? "null" : raw.getClass().getName()).append('\n');
                return sb.toString();
            }
            List<?> infos = (List<?>) raw;
            sb.append("count=").append(infos.size()).append('\n');
            for (int i = 0; i < infos.size(); i++) {
                Object info = infos.get(i);
                if (info == null) continue;
                long timestamp = longCall(info, "getTimestamp");
                String process = stringCall(info, "getProcessName");
                int reason = intCall(info, "getReason");
                int status = intCall(info, "getStatus");
                int importance = intCall(info, "getImportance");
                long pss = longCall(info, "getPss");
                long rss = longCall(info, "getRss");
                int pid = intCall(info, "getPid");
                int subReason = intCall(info, "getSubReason");
                String description = stringCall(info, "getDescription");
                String processState = stringCall(info, "getProcessStateSummary");
                sb.append("--- ").append(i)
                    .append(" time=").append(formatTime(timestamp))
                    .append(" pid=").append(pid)
                    .append(" process=").append(process)
                    .append(" reason=").append(reason)
                    .append(" reasonName=").append(BppDiagnosticBundle.exitReasonName(reason))
                    .append(" subReason=").append(subReason)
                    .append(" subReasonName=").append(BppDiagnosticBundle.exitSubReasonName(subReason))
                    .append(" status=").append(status)
                    .append(" importance=").append(importance)
                    .append(" importanceName=").append(BppDiagnosticBundle.importanceName(importance))
                    .append(" pss=").append(pss)
                    .append(" rss=").append(rss)
                    .append(" description=").append(description)
                    .append(" processStateSummary=").append(processState)
                    .append('\n');
                writeTrace(info, stage, extras, i, process, sb);
            }
        } catch (Throwable error) {
            sb.append("applicationExitInfo=unavailable ").append(error).append('\n');
        }
        return sb.toString();
    }

    private static void writeTrace(
        Object info,
        File stage,
        List<BppDiagnosticBundle.ExtraFile> extras,
        int index,
        String process,
        StringBuilder sb
    ) {
        InputStream in = null;
        try {
            Method method = info.getClass().getMethod("getTraceInputStream");
            Object value = method.invoke(info);
            if (!(value instanceof InputStream)) {
                sb.append("trace=").append(index).append(" none\n");
                return;
            }
            in = (InputStream) value;
            File out = new File(stage, "exit-" + index + "-" + safeToken(process) + ".trace");
            try (FileOutputStream fos = new FileOutputStream(out)) {
                byte[] buf = new byte[8192];
                int total = 0;
                int n;
                while ((n = in.read(buf)) != -1 && total < 2 * 1024 * 1024) {
                    int allow = Math.min(n, 2 * 1024 * 1024 - total);
                    fos.write(buf, 0, allow);
                    total += allow;
                }
                sb.append("trace=").append(index).append(" bytes=").append(total)
                    .append(" file=").append(out.getName()).append('\n');
            }
            extras.add(new BppDiagnosticBundle.ExtraFile(out, "crashes/" + out.getName(), true));
        } catch (Throwable error) {
            sb.append("trace=").append(index).append(" unavailable ").append(error).append('\n');
        } finally {
            if (in != null) {
                try {
                    in.close();
                } catch (Throwable ignored) {
                }
            }
        }
    }

    private static String dumpLogcat(File stage, List<BppDiagnosticBundle.ExtraFile> extras) {
        StringBuilder sb = new StringBuilder();
        sb.append("note=logcat without READ_LOGS only includes this app UID, including :game when the buffer still has it\n");
        writeDump(stage, extras, sb, "all", BppDiagnosticBundle.dumpLogcatCommand("all"));
        for (int i = 0; i < BppDiagnosticBundle.LOGCAT_BUFFERS.length; i++) {
            String buffer = BppDiagnosticBundle.LOGCAT_BUFFERS[i];
            writeDump(stage, extras, sb, buffer, BppDiagnosticBundle.dumpLogcatCommand(buffer));
        }
        return sb.toString();
    }

    private static void writeDump(
        File stage,
        List<BppDiagnosticBundle.ExtraFile> extras,
        StringBuilder sb,
        String name,
        List<String> command
    ) {
        String text = BppDiagnosticBundle.captureProcessText(command, 15000L, 2 * 1024 * 1024);
        File file = new File(stage, "logcat-" + name + ".txt");
        try {
            byte[] data = text.getBytes(StandardCharsets.UTF_8);
            try (FileOutputStream out = new FileOutputStream(file)) {
                out.write(data);
            }
            extras.add(new BppDiagnosticBundle.ExtraFile(file, "logcat/dump-" + name + ".txt", true));
            sb.append(name).append(" bytes=").append(data.length)
                .append(" failed=").append(text.startsWith("[unavailable]") || text.toLowerCase(Locale.ROOT).contains("permission denied"))
                .append('\n');
        } catch (Throwable error) {
            sb.append(name).append(" writeFailed=").append(error).append('\n');
        }
    }

    private static String shellProbes(Context context) {
        StringBuilder sb = new StringBuilder();
        sb.append("[uname]\n").append(capture("uname -a", 4000L, 4096)).append('\n');
        sb.append("[dumpsys activity exit-info]\n");
        sb.append(capture("dumpsys activity exit-info " + context.getPackageName(), 8000L, 400 * 1024)).append('\n');
        sb.append("[dumpsys meminfo]\n");
        sb.append(capture("dumpsys meminfo " + context.getPackageName(), 8000L, 400 * 1024)).append('\n');
        sb.append("[dumpsys display]\n");
        sb.append(capture("dumpsys display", 8000L, 400 * 1024)).append('\n');
        return sb.toString();
    }

    private static String capture(String command, long timeoutMs, int maxBytes) {
        String[] parts = command.split(" ");
        return BppDiagnosticBundle.captureProcessText(Arrays.asList(parts), timeoutMs, maxBytes);
    }

    private static String redactProps(String text) {
        if (text == null) return "";
        String[] lines = text.split("\n", -1);
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < lines.length; i++) {
            String line = lines[i];
            String lower = line.toLowerCase(Locale.ROOT);
            if (lower.contains("password") || lower.contains("secret") || lower.contains("token")
                || lower.contains("keystore")) {
                int cut = line.indexOf(':');
                if (cut < 0) cut = line.indexOf('=');
                sb.append(cut >= 0 ? line.substring(0, cut + 1) + " [redacted]" : "[redacted]");
            } else {
                sb.append(line);
            }
            if (i + 1 < lines.length) sb.append('\n');
        }
        return sb.toString();
    }

    private static void addScan(List<BppDiagnosticBundle.ScanRoot> scans, File dir, String label, boolean stateTree) {
        if (dir == null) {
            scans.add(new BppDiagnosticBundle.ScanRoot(null, label, stateTree));
            return;
        }
        String key;
        try {
            key = dir.getCanonicalPath();
        } catch (Exception ex) {
            key = dir.getAbsolutePath();
        }
        for (int i = 0; i < scans.size(); i++) {
            File existing = scans.get(i).directory;
            if (existing == null) continue;
            try {
                if (key.equals(existing.getCanonicalPath())) return;
            } catch (Exception ignored) {
                if (key.equals(existing.getAbsolutePath())) return;
            }
        }
        scans.add(new BppDiagnosticBundle.ScanRoot(dir, label, stateTree));
    }

    private static void hashIfPresent(StringBuilder sb, String label, File file) {
        appendFileStat(sb, label, file);
        if (file == null || !file.isFile() || !file.canRead() || file.length() > DLL_HASH_CAP) return;
        try {
            sb.append(label).append("Sha256=").append(BppDiagnosticBundle.sha256Hex(file, DLL_HASH_CAP)).append('\n');
        } catch (Throwable error) {
            sb.append(label).append("Sha256=unavailable ").append(error).append('\n');
        }
    }

    private static void appendFileStat(StringBuilder sb, String label, File file) {
        if (file == null) {
            sb.append(label).append("=null\n");
            return;
        }
        sb.append(label).append(" path=").append(file.getAbsolutePath())
            .append(" exists=").append(file.exists())
            .append(" readable=").append(file.canRead())
            .append(" size=").append(file.isFile() ? file.length() : -1)
            .append(" mtime=").append(file.exists() ? formatTime(file.lastModified()) : "0")
            .append('\n');
    }

    private static void appendStat(StringBuilder sb, String label, File dir) {
        try {
            if (dir == null) {
                sb.append(label).append("=null\n");
                return;
            }
            StatFs stat = new StatFs(dir.getAbsolutePath());
            sb.append(label).append(" path=").append(dir.getAbsolutePath())
                .append(" block=").append(stat.getBlockSizeLong())
                .append(" total=").append(stat.getTotalBytes())
                .append(" avail=").append(stat.getAvailableBytes())
                .append('\n');
        } catch (Throwable error) {
            sb.append(label).append("=unavailable ").append(error).append('\n');
        }
    }

    private static void appendSetting(StringBuilder sb, ContentResolver resolver, String key) {
        try {
            sb.append(key).append('=').append(Settings.Global.getString(resolver, key)).append('\n');
        } catch (Throwable error) {
            sb.append(key).append("=unavailable ").append(error.getClass().getSimpleName()).append('\n');
        }
    }

    private static void appendReflected(StringBuilder sb, Class<?> type, String field, String label) {
        try {
            Object value = type.getField(field).get(null);
            sb.append(label).append('=').append(value).append('\n');
        } catch (Throwable ignored) {
            sb.append(label).append("=unavailable\n");
        }
    }

    private static File nativeDir(Context context, String packageName) {
        PackageInfo info = packageOrNull(context, packageName);
        if (info == null || info.applicationInfo == null || info.applicationInfo.nativeLibraryDir == null) return null;
        return new File(info.applicationInfo.nativeLibraryDir);
    }

    private static PackageInfo packageOrNull(Context context, String packageName) {
        try {
            return context.getPackageManager().getPackageInfo(packageName, 0);
        } catch (Throwable ignored) {
            return null;
        }
    }

    private static void addApk(List<File> apks, String path) {
        if (path == null) return;
        File file = new File(path);
        for (int i = 0; i < apks.size(); i++) {
            if (apks.get(i).getAbsolutePath().equals(file.getAbsolutePath())) return;
        }
        apks.add(file);
    }

    private static String readVersion(Context context) {
        try {
            return context.getPackageManager().getPackageInfo(context.getPackageName(), 0).versionName;
        } catch (Throwable ignored) {
            return "未知";
        }
    }

    private static String formatTime(long millis) {
        if (millis <= 0L) return "0";
        try {
            return Instant.ofEpochMilli(millis).toString() + " (" + millis + ")";
        } catch (RuntimeException ex) {
            return Long.toString(millis);
        }
    }

    private static String readHead(File file, int maxBytes) {
        if (file == null || !file.isFile() || !file.canRead()) return "(unreadable)\n";
        try (InputStream in = new FileInputStream(file)) {
            byte[] data = new byte[Math.max(1, maxBytes)];
            int off = 0;
            while (off < data.length) {
                int n = in.read(data, off, data.length - off);
                if (n < 0) break;
                off += n;
            }
            return new String(data, 0, off, StandardCharsets.UTF_8);
        } catch (Throwable error) {
            return "(unreadable) " + error.getMessage() + "\n";
        }
    }

    private static long longCall(Object target, String name) {
        try {
            Object value = target.getClass().getMethod(name).invoke(target);
            if (value instanceof Long) return ((Long) value).longValue();
            if (value instanceof Integer) return ((Integer) value).longValue();
        } catch (Throwable ignored) {
        }
        return -1L;
    }

    private static int intCall(Object target, String name) {
        try {
            Object value = target.getClass().getMethod(name).invoke(target);
            if (value instanceof Integer) return ((Integer) value).intValue();
            if (value instanceof Long) return ((Long) value).intValue();
        } catch (Throwable ignored) {
        }
        return -1;
    }

    private static String stringCall(Object target, String name) {
        try {
            Object value = target.getClass().getMethod(name).invoke(target);
            return value == null ? "" : String.valueOf(value);
        } catch (Throwable error) {
            return "unavailable";
        }
    }

    static String latestGameExitNote(Context context) {
        if (context == null || Build.VERSION.SDK_INT < 30) return "";
        try {
            ActivityManager am = (ActivityManager) context.getSystemService(Context.ACTIVITY_SERVICE);
            if (am == null) return "";
            Method historical = ActivityManager.class.getMethod(
                "getHistoricalProcessExitReasons", String.class, int.class, int.class);
            Object raw = historical.invoke(am, context.getPackageName(), Integer.valueOf(0), Integer.valueOf(20));
            if (!(raw instanceof List)) return "";
            List<?> infos = (List<?>) raw;
            for (int i = 0; i < infos.size(); i++) {
                Object info = infos.get(i);
                if (info == null) continue;
                String process = stringCall(info, "getProcessName");
                if (process == null || !process.endsWith(":game")) continue;
                int reason = intCall(info, "getReason");
                int importance = intCall(info, "getImportance");
                return "reason=" + reason
                    + " reasonName=" + BppDiagnosticBundle.exitReasonName(reason)
                    + " importance=" + importance
                    + " importanceName=" + BppDiagnosticBundle.importanceName(importance)
                    + " time=" + formatTime(longCall(info, "getTimestamp"));
            }
        } catch (Throwable ignored) {
        }
        return "";
    }

    private static String sha256(byte[] data) throws Exception {
        return hex(MessageDigest.getInstance("SHA-256").digest(data));
    }

    private static String hex(byte[] data) {
        char[] alphabet = "0123456789abcdef".toCharArray();
        char[] out = new char[data.length * 2];
        for (int i = 0; i < data.length; i++) {
            int v = data[i] & 0xff;
            out[i * 2] = alphabet[v >>> 4];
            out[i * 2 + 1] = alphabet[v & 0x0f];
        }
        return new String(out);
    }

    private static String safeToken(String value) {
        if (value == null || value.length() == 0) return "unknown";
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < value.length() && sb.length() < 48; i++) {
            char c = value.charAt(i);
            boolean ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || c == '.' || c == '_' || c == '-';
            sb.append(ok ? c : '_');
        }
        return sb.toString();
    }

    private static String safeSection(List<String> errors, String name, Section section) {
        try {
            String value = section.run();
            return value == null ? "" : value;
        } catch (Throwable error) {
            errors.add(name + ": " + error.getClass().getSimpleName() + ": " + error.getMessage());
            return "[unavailable] " + name + " " + error + "\n";
        }
    }

    private static void deleteRecursively(File file) {
        if (file == null || !file.exists()) return;
        if (file.isDirectory()) {
            File[] children = file.listFiles();
            if (children != null) {
                for (int i = 0; i < children.length; i++) deleteRecursively(children[i]);
            }
        }
        file.delete();
    }

    private interface Section {
        String run() throws Exception;
    }
}
