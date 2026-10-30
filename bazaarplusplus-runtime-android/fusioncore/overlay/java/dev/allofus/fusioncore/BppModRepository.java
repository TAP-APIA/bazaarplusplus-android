package dev.allofus.fusioncore;

import android.content.Context;
import android.net.Uri;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Locale;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;

import dev.allofus.fusioncore.tools.Utilities;

final class BppModRepository {
    static final String GAME_PACKAGE = "com.tempogames.thebazaar";
    private static final String PREFS = "bpp_launcher";
    private static final String BUILTIN_ENABLED = "builtin_enabled";
    private static final long MAX_IMPORT_BYTES = 256L * 1024L * 1024L;

    private BppModRepository() {}

    static File gameRoot() {
        return Utilities.getExternalFusionCoreDirectory(GAME_PACKAGE, null);
    }

    static File bepInExRoot() { return new File(gameRoot(), "BepInEx"); }
    static File pluginsRoot() { return new File(bepInExRoot(), "plugins"); }
    static File enabledImports() { return new File(pluginsRoot(), "imported"); }
    static File disabledImports() { return new File(bepInExRoot(), "disabled-plugins"); }
    static File logFile() { return new File(bepInExRoot(), "LogOutput.log"); }

    static boolean isBuiltInEnabled(Context context) {
        return context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .getBoolean(BUILTIN_ENABLED, true);
    }

    static void setBuiltInEnabled(Context context, boolean enabled) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
                .putBoolean(BUILTIN_ENABLED, enabled).apply();
    }

    static void applyBuiltInState(Context context, File bepInExDir) {
        if (isBuiltInEnabled(context)) return;
        File plugins = new File(bepInExDir, "plugins");
        String[] owned = {
                "BazaarPlusPlus.dll",
                "BazaarPlusPlus.ModApi.dll",
                "BazaarPlusPlus.Storage.dll",
                "BazaarPlusPlus.Localization.dll"
        };
        for (String name : owned) new File(plugins, name).delete();
    }

    static List<ImportedMod> listImported() {
        List<ImportedMod> result = new ArrayList<>();
        collect(result, enabledImports(), true);
        collect(result, disabledImports(), false);
        Collections.sort(result, (a, b) -> a.name.compareToIgnoreCase(b.name));
        return result;
    }

    private static void collect(List<ImportedMod> target, File root, boolean enabled) {
        File[] dirs = root.listFiles(File::isDirectory);
        if (dirs == null) return;
        for (File dir : dirs) target.add(new ImportedMod(dir.getName(), enabled, dir));
    }

    static String importUri(Context context, Uri uri, String displayName) throws IOException {
        String lower = displayName.toLowerCase(Locale.ROOT);
        String id = safeId(displayName.replaceFirst("\\.[^.]+$", ""));
        if (id.isEmpty()) id = "mod-" + System.currentTimeMillis();
        File destination = uniqueDirectory(enabledImports(), id);
        if (!destination.mkdirs()) throw new IOException("Cannot create " + destination);
        boolean success = false;
        try (InputStream input = context.getContentResolver().openInputStream(uri)) {
            if (input == null) throw new IOException("Cannot open selected file");
            if (lower.endsWith(".dll")) {
                copyBounded(input, new File(destination, safeDllName(displayName)));
            } else if (lower.endsWith(".zip")) {
                extractDllZip(input, destination);
            } else {
                throw new IOException("Only .dll and .zip files are supported");
            }
            File[] dlls = destination.listFiles((dir, name) -> name.toLowerCase(Locale.ROOT).endsWith(".dll"));
            if (dlls == null || dlls.length == 0) throw new IOException("No DLL found in package");
            success = true;
            return destination.getName();
        } finally {
            if (!success) Utilities.deleteRecursive(destination);
        }
    }

    private static void extractDllZip(InputStream input, File destination) throws IOException {
        long total = 0;
        byte[] buffer = new byte[32 * 1024];
        try (ZipInputStream zip = new ZipInputStream(input)) {
            ZipEntry entry;
            while ((entry = zip.getNextEntry()) != null) {
                if (entry.isDirectory()) continue;
                String leaf = new File(entry.getName()).getName();
                if (!leaf.toLowerCase(Locale.ROOT).endsWith(".dll")) continue;
                File target = new File(destination, safeDllName(leaf));
                try (FileOutputStream output = new FileOutputStream(target)) {
                    int count;
                    while ((count = zip.read(buffer)) > 0) {
                        total += count;
                        if (total > MAX_IMPORT_BYTES) throw new IOException("Package is too large");
                        output.write(buffer, 0, count);
                    }
                }
            }
        }
    }

    private static void copyBounded(InputStream input, File target) throws IOException {
        long total = 0;
        byte[] buffer = new byte[32 * 1024];
        try (FileOutputStream output = new FileOutputStream(target)) {
            int count;
            while ((count = input.read(buffer)) > 0) {
                total += count;
                if (total > MAX_IMPORT_BYTES) throw new IOException("File is too large");
                output.write(buffer, 0, count);
            }
        }
    }

    static boolean setEnabled(ImportedMod mod, boolean enabled) {
        File root = enabled ? enabledImports() : disabledImports();
        if (!root.exists() && !root.mkdirs()) return false;
        return mod.directory.renameTo(uniqueDirectory(root, mod.name));
    }

    static boolean delete(ImportedMod mod) { return Utilities.deleteRecursive(mod.directory); }

    private static File uniqueDirectory(File root, String base) {
        File value = new File(root, base);
        int suffix = 2;
        while (value.exists()) value = new File(root, base + "-" + suffix++);
        return value;
    }

    private static String safeId(String value) {
        return value.replaceAll("[^A-Za-z0-9._-]", "-").replaceAll("-+", "-");
    }

    private static String safeDllName(String value) {
        String leaf = new File(value).getName();
        return safeId(leaf).toLowerCase(Locale.ROOT).endsWith(".dll") ? safeId(leaf) : safeId(leaf) + ".dll";
    }

    static final class ImportedMod {
        final String name;
        final boolean enabled;
        final File directory;
        ImportedMod(String name, boolean enabled, File directory) {
            this.name = name;
            this.enabled = enabled;
            this.directory = directory;
        }
    }
}
