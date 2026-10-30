package com.bepinex.android.bpp;

import java.io.BufferedInputStream;
import java.io.BufferedReader;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.RandomAccessFile;
import java.nio.channels.FileChannel;
import java.nio.channels.FileLock;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;

/**
 * Pure-Java installer and verifier for the bundled BazaarPlusPlus Android modpack.
 *
 * Owns only modpack.json (create/repair-only) and the listed files under plugins/ and core/
 * in both modpacks/bpp/ and BepInEx/. Never modifies or deletes user config, logs, or
 * BazaarPlusPlusV6 history databases.
 */
public final class BppBundledModpackInstaller {

    public static final String TARGET_PACKAGE = "com.tempogames.thebazaar";
    public static final String MODPACK_NAME = "bpp";
    public static final String ASSET_MODPACK_ZIP = "bpp-modpack.zip";
    public static final String ASSET_MODPACK_MANIFEST = "bpp-modpack-manifest.tsv";
    public static final String ASSET_BEPINEX_ZIP = "BepInEx.Android.zip";
    public static final String STATE_FILE_NAME = "bpp-bundled-modpack.state";
    public static final String LOCK_FILE_NAME = "bpp-bundled-modpack.lock";

    private static final Object PROCESS_LOCK = new Object();
    private static final Pattern MOD_COUNT_PATTERN = Pattern.compile("\"modCount\"\\s*:\\s*\\d+");

    private static final String[] CRITICAL_OWNED_PATHS = new String[] {
        "modpack.json",
        "plugins/BazaarPlusPlus.ModApi.dll",
        "plugins/BazaarPlusPlus.Storage.dll",
        "plugins/Microsoft.Data.Sqlite.dll",
        "plugins/SQLitePCLRaw.core.dll",
        "plugins/SQLitePCLRaw.provider.e_sqlite3.dll",
        "plugins/libe_sqlite3.so",
        "core/BepInEx.Unity.IL2CPP.dll",
        "core/BepInEx.Core.dll",
        "core/Il2CppInterop.Runtime.dll",
        "core/Il2CppInterop.HarmonySupport.dll"
    };

    // The plugin itself, one complete set: this fork's own plugin, or upstream's Android Host
    // behind the BepInEx bridge (android-port/upstream-host/README.md). Switching between them is
    // an ordinary update: files the previous manifest owned are retired.
    private static final String[][] PLUGIN_PAYLOAD_VARIANTS = new String[][] {
        {
            "plugins/BazaarPlusPlus.dll",
            "plugins/BazaarPlusPlus.Localization.dll",
            "plugins/Bpp.Managed.MessagePack.dll",
            "plugins/Bpp.Managed.MessagePack.Annotations.dll",
            "plugins/Bpp.Managed.StringTools.dll",
            "plugins/SQLitePCLRaw.batteries_v2.dll"
        },
        {
            "plugins/BazaarPlusPlus.BepInExBridge.dll",
            "plugins/BazaarPlusPlus.Android.dll",
            "plugins/BazaarPlusPlus.Core.dll",
            "plugins/MelonLoader.dll",
            "plugins/MessagePack.dll",
            "plugins/Newtonsoft.Json.dll"
        }
    };

    public interface StreamSupplier {
        InputStream open() throws IOException;
    }

    public interface ProgressListener {
        void onProgress(String step, int percent);
    }

    public enum StatusKind {
        READY,
        NEEDS_INSTALL,
        NEEDS_UPDATE,
        NEEDS_REPAIR
    }

    public static final class ManifestEntry {
        public final String relativePath;
        public final long size;
        public final String sha256;

        public ManifestEntry(String relativePath, long size, String sha256) {
            this.relativePath = relativePath;
            this.size = size;
            this.sha256 = sha256.toLowerCase(Locale.ROOT);
        }
    }

    public static final class Manifest {
        public final String bundleId;
        public final String version;
        public final Map<String, ManifestEntry> entries;
        public final int managedDllCount;

        public Manifest(String bundleId, String version, Map<String, ManifestEntry> entries) {
            this.bundleId = bundleId.toLowerCase(Locale.ROOT);
            this.version = version;
            this.entries = Collections.unmodifiableMap(new LinkedHashMap<String, ManifestEntry>(entries));
            int dlls = 0;
            for (String path : entries.keySet()) {
                if (path.startsWith("plugins/") && path.toLowerCase(Locale.ROOT).endsWith(".dll")) {
                    dlls++;
                }
            }
            this.managedDllCount = dlls;
        }
    }

    public static final class StatusReport {
        public final StatusKind kind;
        public final String bundleId;
        public final String version;
        public final String detail;

        public StatusReport(StatusKind kind, String bundleId, String version, String detail) {
            this.kind = kind;
            this.bundleId = bundleId;
            this.version = version;
            this.detail = detail;
        }

        public boolean isReady() {
            return kind == StatusKind.READY;
        }
    }

    private static final class InstalledState {
        final boolean committed;
        final String bundleId;
        final Map<String, String> ownedFileHashes;

        InstalledState(boolean committed, String bundleId, Map<String, String> ownedFileHashes) {
            this.committed = committed;
            this.bundleId = bundleId;
            this.ownedFileHashes = ownedFileHashes;
        }
    }

    private BppBundledModpackInstaller() {}

    public static Manifest parseManifest(InputStream stream) throws IOException {
        if (stream == null) {
            throw new IOException("Missing bundled modpack manifest stream.");
        }
        BufferedReader reader = new BufferedReader(new InputStreamReader(stream, StandardCharsets.UTF_8));
        String bundleId = null;
        String version = null;
        Map<String, ManifestEntry> entries = new LinkedHashMap<String, ManifestEntry>();
        String rawLine;
        while ((rawLine = reader.readLine()) != null) {
            String line = rawLine.trim();
            if (line.isEmpty() || line.startsWith("#")) {
                continue;
            }
            String[] parts = line.split("\t");
            if ("bundle_id".equals(parts[0])) {
                if (parts.length < 3) {
                    throw new IOException("Malformed bundle_id header in manifest.");
                }
                bundleId = requireSha256(parts[1], "bundle_id");
                version = parts[2].trim();
                if (version.isEmpty()) {
                    throw new IOException("Empty bundle version in manifest.");
                }
            } else if ("file".equals(parts[0])) {
                if (parts.length != 4) {
                    throw new IOException("Malformed file entry in manifest: " + line);
                }
                String path = validateOwnedRelativePath(parts[1]);
                long size;
                try {
                    size = Long.parseLong(parts[2]);
                } catch (NumberFormatException ex) {
                    throw new IOException("Invalid file size for " + path, ex);
                }
                if (size <= 0L) {
                    throw new IOException("Non-positive file size for " + path);
                }
                String sha = requireSha256(parts[3], path);
                if (entries.put(path, new ManifestEntry(path, size, sha)) != null) {
                    throw new IOException("Duplicate manifest path: " + path);
                }
            } else {
                throw new IOException("Unknown manifest record type: " + parts[0]);
            }
        }
        if (bundleId == null || version == null) {
            throw new IOException("Manifest is missing bundle_id header.");
        }
        for (String required : CRITICAL_OWNED_PATHS) {
            if (!entries.containsKey(required)) {
                throw new IOException("Manifest is missing required payload entry: " + required);
            }
        }
        if (!containsAnyVariant(entries.keySet())) {
            throw new IOException(
                "Manifest carries no complete plugin payload: expected all of "
                    + String.join(", ", PLUGIN_PAYLOAD_VARIANTS[0])
                    + " or all of "
                    + String.join(", ", PLUGIN_PAYLOAD_VARIANTS[1])
            );
        }
        return new Manifest(bundleId, version, entries);
    }

    private static boolean containsAnyVariant(Set<String> paths) {
        for (String[] variant : PLUGIN_PAYLOAD_VARIANTS) {
            boolean complete = true;
            for (String path : variant) {
                complete &= paths.contains(path);
            }
            if (complete) {
                return true;
            }
        }
        return false;
    }

    public static StatusReport inspectStatus(
        File gameRootDir,
        File internalWorkDir,
        Manifest manifest,
        boolean verifyHashes
    ) {
        File stateFile = new File(internalWorkDir, STATE_FILE_NAME);
        InstalledState state = readInstalledState(stateFile);
        File modpackDir = new File(gameRootDir, "modpacks/" + MODPACK_NAME);
        File runtimeDir = new File(gameRootDir, "BepInEx");
        if (state == null) {
            boolean anyExisting = false;
            for (String plugin : new String[] { "plugins/BazaarPlusPlus.dll", "plugins/BazaarPlusPlus.BepInExBridge.dll" }) {
                anyExisting |= new File(modpackDir, plugin).exists() || new File(runtimeDir, plugin).exists();
            }
            return new StatusReport(
                anyExisting ? StatusKind.NEEDS_UPDATE : StatusKind.NEEDS_INSTALL,
                manifest.bundleId,
                manifest.version,
                anyExisting ? "检测到已有模组目录，待同步到当前内置版本" : "首次启动待部署内置 BPP 模组包"
            );
        }
        if (!state.committed) {
            return new StatusReport(
                StatusKind.NEEDS_REPAIR,
                manifest.bundleId,
                manifest.version,
                "上次部署未完成，待自动恢复"
            );
        }
        if (!manifest.bundleId.equalsIgnoreCase(state.bundleId)) {
            return new StatusReport(
                StatusKind.NEEDS_UPDATE,
                manifest.bundleId,
                manifest.version,
                "发现新版内置模组包，待自动更新"
            );
        }
        String verificationFailure = verifyInstalledFiles(gameRootDir, manifest, verifyHashes);
        if (verificationFailure != null) {
            return new StatusReport(
                StatusKind.NEEDS_REPAIR,
                manifest.bundleId,
                manifest.version,
                verificationFailure
            );
        }
        return new StatusReport(
            StatusKind.READY,
            manifest.bundleId,
            manifest.version,
            "内置 BPP 模组已就绪"
        );
    }

    public static StatusReport installOrUpdate(
        File gameRootDir,
        File internalWorkDir,
        StreamSupplier manifestSupplier,
        StreamSupplier modpackZipSupplier,
        StreamSupplier bepInExFrameworkZipSupplier,
        boolean forceRepair,
        ProgressListener progress
    ) throws IOException {
        if (gameRootDir == null || internalWorkDir == null) {
            throw new IOException("Game root and internal work directories are required.");
        }
        Manifest manifest;
        try (InputStream manifestStream = manifestSupplier.open()) {
            manifest = parseManifest(manifestStream);
        }
        synchronized (PROCESS_LOCK) {
            ensureDir(internalWorkDir);
            File lockFile = new File(internalWorkDir, LOCK_FILE_NAME);
            try (
                RandomAccessFile raf = new RandomAccessFile(lockFile, "rw");
                FileChannel channel = raf.getChannel();
                FileLock fileLock = channel.lock()
            ) {
                if (!forceRepair) {
                    StatusReport current = inspectStatus(gameRootDir, internalWorkDir, manifest, true);
                    if (current.isReady()) {
                        reconcileConfigsAdditively(gameRootDir, bepInExFrameworkZipSupplier);
                        notifyProgress(progress, "内置 BPP 模组已就绪", 100);
                        return current;
                    }
                }

                notifyProgress(progress, "正在校验内置模组包…", 15);
                File stateFile = new File(internalWorkDir, STATE_FILE_NAME);
                InstalledState previousState = readInstalledState(stateFile);
                Map<String, String> previousOwned = previousState != null
                    ? previousState.ownedFileHashes
                    : Collections.<String, String>emptyMap();

                File stagingDir = new File(internalWorkDir, "staging-" + System.nanoTime());
                deleteRecursively(stagingDir);
                ensureDir(stagingDir);
                try {
                    stageAndVerifyZip(modpackZipSupplier, manifest, stagingDir);
                    writeStateFile(stateFile, false, manifest.bundleId, previousOwned);

                    notifyProgress(progress, "正在保护配置文件与同步运行环境…", 45);
                    reconcileConfigsAdditively(gameRootDir, bepInExFrameworkZipSupplier);

                    notifyProgress(progress, "正在写入插件与修补核心…", 70);
                    applyStagedFiles(gameRootDir, stagingDir, manifest, previousOwned);

                    notifyProgress(progress, "正在校验部署完整性…", 90);
                    String failure = verifyInstalledFiles(gameRootDir, manifest, true);
                    if (failure != null) {
                        throw new IOException("Post-install verification failed: " + failure);
                    }

                    Map<String, String> committedOwned = new LinkedHashMap<String, String>();
                    for (ManifestEntry entry : manifest.entries.values()) {
                        if (!"modpack.json".equals(entry.relativePath)) {
                            committedOwned.put(entry.relativePath, entry.sha256);
                        }
                    }
                    writeStateFile(stateFile, true, manifest.bundleId, committedOwned);
                } finally {
                    deleteRecursively(stagingDir);
                }

                notifyProgress(progress, "内置 BPP 模组部署完成", 100);
                return new StatusReport(
                    StatusKind.READY,
                    manifest.bundleId,
                    manifest.version,
                    "内置 BPP 模组已就绪"
                );
            }
        }
    }

    private static void stageAndVerifyZip(
        StreamSupplier modpackZipSupplier,
        Manifest manifest,
        File stagingDir
    ) throws IOException {
        Set<String> seenEntries = new LinkedHashSet<String>();
        String canonicalStagingRoot = stagingDir.getCanonicalPath() + File.separator;
        try (
            InputStream raw = modpackZipSupplier.open();
            ZipInputStream zis = new ZipInputStream(new BufferedInputStream(raw))
        ) {
            ZipEntry entry;
            while ((entry = zis.getNextEntry()) != null) {
                String rawName = entry.getName();
                if (rawName == null || rawName.isEmpty()) {
                    continue;
                }
                if (rawName.indexOf('\\') >= 0) {
                    throw new IOException("Backslash path separator rejected in archive: " + rawName);
                }
                if (entry.isDirectory() || rawName.endsWith("/")) {
                    validateDirectoryEntry(rawName);
                    continue;
                }
                String relativePath = validateOwnedRelativePath(rawName);
                if (!seenEntries.add(relativePath)) {
                    throw new IOException("Duplicate archive entry: " + relativePath);
                }
                ManifestEntry expected = manifest.entries.get(relativePath);
                if (expected == null) {
                    throw new IOException("Unlisted archive entry rejected: " + relativePath);
                }
                File outFile = new File(stagingDir, relativePath);
                String canonicalTarget = outFile.getCanonicalPath();
                if (!canonicalTarget.startsWith(canonicalStagingRoot)) {
                    throw new IOException("Archive entry escaped staging root: " + relativePath);
                }
                ensureDir(outFile.getParentFile());
                MessageDigest digest = newSha256();
                long written = 0L;
                try (FileOutputStream fos = new FileOutputStream(outFile)) {
                    byte[] buffer = new byte[16 * 1024];
                    int read;
                    while ((read = zis.read(buffer)) != -1) {
                        written += read;
                        if (written > expected.size) {
                            throw new IOException("Archive entry exceeded expected size: " + relativePath);
                        }
                        digest.update(buffer, 0, read);
                        fos.write(buffer, 0, read);
                    }
                    fos.getFD().sync();
                }
                if (written != expected.size) {
                    throw new IOException(
                        "Archive entry size mismatch for " + relativePath
                            + ": expected " + expected.size + ", got " + written
                    );
                }
                String actualSha = toHex(digest.digest());
                if (!expected.sha256.equalsIgnoreCase(actualSha)) {
                    throw new IOException("Archive entry SHA-256 mismatch for " + relativePath);
                }
            }
        }
        if (seenEntries.size() != manifest.entries.size()) {
            throw new IOException(
                "Archive entry count mismatch: expected " + manifest.entries.size()
                    + ", got " + seenEntries.size()
            );
        }
    }

    private static void applyStagedFiles(
        File gameRootDir,
        File stagingDir,
        Manifest manifest,
        Map<String, String> previousOwned
    ) throws IOException {
        File modpackDir = new File(gameRootDir, "modpacks/" + MODPACK_NAME);
        File runtimeDir = new File(gameRootDir, "BepInEx");
        ensureDir(new File(modpackDir, "plugins"));
        ensureDir(new File(modpackDir, "core"));
        ensureDir(new File(modpackDir, "config"));
        ensureDir(new File(modpackDir, "logs"));
        ensureDir(new File(runtimeDir, "plugins"));
        ensureDir(new File(runtimeDir, "core"));
        ensureDir(new File(runtimeDir, "config"));

        for (ManifestEntry entry : manifest.entries.values()) {
            String rel = entry.relativePath;
            File staged = new File(stagingDir, rel);
            if ("modpack.json".equals(rel)) {
                updateOrCreateModpackJson(
                    new File(modpackDir, "modpack.json"),
                    staged,
                    manifest.managedDllCount
                );
                continue;
            }
            atomicCopyIfNeeded(staged, new File(modpackDir, rel), entry.size, entry.sha256);
            atomicCopyIfNeeded(staged, new File(runtimeDir, rel), entry.size, entry.sha256);
        }

        for (Map.Entry<String, String> oldEntry : previousOwned.entrySet()) {
            String oldRel = oldEntry.getKey();
            String oldSha = oldEntry.getValue();
            if ("modpack.json".equals(oldRel) || manifest.entries.containsKey(oldRel)) {
                continue;
            }
            if (!isAllowedOwnedSubpath(oldRel)) {
                continue;
            }
            deleteIfUnmodifiedOwnedFile(new File(modpackDir, oldRel), oldSha);
            deleteIfUnmodifiedOwnedFile(new File(runtimeDir, oldRel), oldSha);
        }
    }

    private static void updateOrCreateModpackJson(
        File targetFile,
        File stagedFile,
        int managedDllCount
    ) throws IOException {
        if (targetFile.isFile() && targetFile.length() > 0L) {
            String existing = readUtf8(targetFile).trim();
            if (isValidModpackJson(existing)) {
                Matcher matcher = MOD_COUNT_PATTERN.matcher(existing);
                String updated = matcher.find()
                    ? matcher.replaceFirst("\"modCount\":" + managedDllCount)
                    : existing;
                if (!updated.equals(existing)) {
                    atomicWriteUtf8(targetFile, updated);
                }
                return;
            }
        }
        atomicCopyFile(stagedFile, targetFile);
    }

    private static boolean isValidModpackJson(String text) {
        if (text == null || !text.startsWith("{") || !text.endsWith("}")) {
            return false;
        }
        boolean hasName = Pattern.compile("\"name\"\\s*:\\s*\"" + Pattern.quote(MODPACK_NAME) + "\"")
            .matcher(text)
            .find();
        boolean hasPackage = Pattern.compile("\"packageName\"\\s*:\\s*\"" + Pattern.quote(TARGET_PACKAGE) + "\"")
            .matcher(text)
            .find();
        return hasName && hasPackage;
    }

    /**
     * Ensures modpacks/bpp/config and BepInEx/config both carry BepInEx.cfg and any existing
     * user .cfg files before BootstrapActivity runs ModpackManager.restoreRuntimeState(),
     * which otherwise wipes BepInEx/config on a first switch from vanilla to bpp.
     */
    public static void reconcileConfigsAdditively(
        File gameRootDir,
        StreamSupplier bepInExFrameworkZipSupplier
    ) throws IOException {
        File runtimeConfigDir = new File(gameRootDir, "BepInEx/config");
        File modpackConfigDir = new File(gameRootDir, "modpacks/" + MODPACK_NAME + "/config");
        ensureDir(runtimeConfigDir);
        ensureDir(modpackConfigDir);

        File runtimeBepCfg = new File(runtimeConfigDir, "BepInEx.cfg");
        File modpackBepCfg = new File(modpackConfigDir, "BepInEx.cfg");
        if (!runtimeBepCfg.isFile() && !modpackBepCfg.isFile() && bepInExFrameworkZipSupplier != null) {
            byte[] defaultCfg = extractSingleEntryFromZip(bepInExFrameworkZipSupplier, "config/BepInEx.cfg");
            if (defaultCfg != null && defaultCfg.length > 0) {
                atomicWriteBytes(runtimeBepCfg, defaultCfg);
                atomicWriteBytes(modpackBepCfg, defaultCfg);
            }
        }

        syncAuthoritativeRuntimeConfigsToModpack(runtimeConfigDir, modpackConfigDir);
        copyMissingCfgFiles(modpackConfigDir, runtimeConfigDir);
    }

    private static void syncAuthoritativeRuntimeConfigsToModpack(File runtimeConfigDir, File modpackConfigDir)
        throws IOException {
        File[] files = runtimeConfigDir.listFiles();
        if (files == null) {
            return;
        }
        for (File file : files) {
            if (!file.isFile() || file.length() == 0L || !file.getName().toLowerCase(Locale.ROOT).endsWith(".cfg")) {
                continue;
            }
            File dest = new File(modpackConfigDir, file.getName());
            String sha = sha256File(file);
            atomicCopyIfNeeded(file, dest, file.length(), sha);
        }
    }

    private static void copyMissingCfgFiles(File sourceDir, File destDir) throws IOException {
        File[] files = sourceDir.listFiles();
        if (files == null) {
            return;
        }
        for (File file : files) {
            if (!file.isFile() || !file.getName().toLowerCase(Locale.ROOT).endsWith(".cfg")) {
                continue;
            }
            File dest = new File(destDir, file.getName());
            if (!dest.exists() || dest.length() == 0L) {
                atomicCopyFile(file, dest);
            }
        }
    }

    private static byte[] extractSingleEntryFromZip(
        StreamSupplier zipSupplier,
        String wantedEntry
    ) throws IOException {
        try (
            InputStream raw = zipSupplier.open();
            ZipInputStream zis = new ZipInputStream(new BufferedInputStream(raw))
        ) {
            ZipEntry entry;
            while ((entry = zis.getNextEntry()) != null) {
                String name = entry.getName().replace('\\', '/');
                if (!entry.isDirectory() && wantedEntry.equals(name)) {
                    ByteArrayOutputStream buffer = new ByteArrayOutputStream();
                    byte[] chunk = new byte[8192];
                    int read;
                    while ((read = zis.read(chunk)) != -1) {
                        buffer.write(chunk, 0, read);
                    }
                    return buffer.toByteArray();
                }
            }
        }
        return null;
    }

    private static String verifyInstalledFiles(
        File gameRootDir,
        Manifest manifest,
        boolean verifyHashes
    ) {
        File modpackDir = new File(gameRootDir, "modpacks/" + MODPACK_NAME);
        File runtimeDir = new File(gameRootDir, "BepInEx");
        File modpackJson = new File(modpackDir, "modpack.json");
        if (!modpackJson.isFile() || modpackJson.length() == 0L) {
            return "缺少 modpack.json";
        }
        try {
            if (!isValidModpackJson(readUtf8(modpackJson).trim())) {
                return "modpack.json 内容无效";
            }
        } catch (IOException ex) {
            return "无法读取 modpack.json";
        }

        for (ManifestEntry entry : manifest.entries.values()) {
            if ("modpack.json".equals(entry.relativePath)) {
                continue;
            }
            File modpackCopy = new File(modpackDir, entry.relativePath);
            File runtimeCopy = new File(runtimeDir, entry.relativePath);
            if (!matchesFile(modpackCopy, entry.size, entry.sha256, verifyHashes)) {
                return "模组源目录文件待修复: " + entry.relativePath;
            }
            if (!matchesFile(runtimeCopy, entry.size, entry.sha256, verifyHashes)) {
                return "运行目录文件待修复: " + entry.relativePath;
            }
        }
        return null;
    }

    private static boolean matchesFile(File file, long expectedSize, String expectedSha, boolean verifyHash) {
        if (!file.isFile() || file.length() != expectedSize) {
            return false;
        }
        if (!verifyHash) {
            return true;
        }
        try {
            return expectedSha.equalsIgnoreCase(sha256File(file));
        } catch (IOException ex) {
            return false;
        }
    }

    private static void atomicCopyIfNeeded(
        File source,
        File target,
        long expectedSize,
        String expectedSha
    ) throws IOException {
        if (matchesFile(target, expectedSize, expectedSha, true)) {
            return;
        }
        atomicCopyFile(source, target);
    }

    private static void atomicCopyFile(File source, File target) throws IOException {
        ensureDir(target.getParentFile());
        File temp = new File(target.getParentFile(), "." + target.getName() + ".bpp-tmp");
        try (
            FileInputStream in = new FileInputStream(source);
            FileOutputStream out = new FileOutputStream(temp)
        ) {
            byte[] buffer = new byte[16 * 1024];
            int read;
            while ((read = in.read(buffer)) != -1) {
                out.write(buffer, 0, read);
            }
            out.getFD().sync();
        }
        replaceFileAtomically(temp, target);
    }

    private static void atomicWriteUtf8(File target, String content) throws IOException {
        atomicWriteBytes(target, content.getBytes(StandardCharsets.UTF_8));
    }

    private static void atomicWriteBytes(File target, byte[] bytes) throws IOException {
        ensureDir(target.getParentFile());
        File temp = new File(target.getParentFile(), "." + target.getName() + ".bpp-tmp");
        try (FileOutputStream out = new FileOutputStream(temp)) {
            out.write(bytes);
            out.getFD().sync();
        }
        replaceFileAtomically(temp, target);
    }

    private static void replaceFileAtomically(File temp, File target) throws IOException {
        try {
            Files.move(
                temp.toPath(),
                target.toPath(),
                StandardCopyOption.REPLACE_EXISTING,
                StandardCopyOption.ATOMIC_MOVE
            );
        } catch (IOException atomicEx) {
            Files.move(temp.toPath(), target.toPath(), StandardCopyOption.REPLACE_EXISTING);
        }
    }

    private static void deleteIfUnmodifiedOwnedFile(File file, String expectedSha) {
        if (!file.isFile()) {
            return;
        }
        try {
            if (expectedSha.equalsIgnoreCase(sha256File(file))) {
                file.delete();
            }
        } catch (IOException ignored) {
        }
    }

    private static InstalledState readInstalledState(File stateFile) {
        if (!stateFile.isFile()) {
            return null;
        }
        try {
            String content = readUtf8(stateFile);
            boolean committed = false;
            String bundleId = "";
            Map<String, String> owned = new LinkedHashMap<String, String>();
            for (String rawLine : content.split("\n")) {
                String line = rawLine.trim();
                if (line.isEmpty()) {
                    continue;
                }
                String[] parts = line.split("\t");
                if ("status".equals(parts[0]) && parts.length >= 2) {
                    committed = "committed".equals(parts[1]);
                } else if ("bundle_id".equals(parts[0]) && parts.length >= 2) {
                    bundleId = parts[1].trim().toLowerCase(Locale.ROOT);
                } else if ("owned".equals(parts[0]) && parts.length >= 3) {
                    String rel = parts[1].trim();
                    String sha = parts[2].trim().toLowerCase(Locale.ROOT);
                    if (isAllowedOwnedSubpath(rel) && isSha256(sha)) {
                        owned.put(rel, sha);
                    }
                }
            }
            if (bundleId.isEmpty()) {
                return null;
            }
            return new InstalledState(committed, bundleId, owned);
        } catch (IOException ex) {
            return null;
        }
    }

    private static void writeStateFile(
        File stateFile,
        boolean committed,
        String bundleId,
        Map<String, String> ownedHashes
    ) throws IOException {
        StringBuilder sb = new StringBuilder();
        sb.append("status\t").append(committed ? "committed" : "in_progress").append('\n');
        sb.append("bundle_id\t").append(bundleId.toLowerCase(Locale.ROOT)).append('\n');
        for (Map.Entry<String, String> entry : ownedHashes.entrySet()) {
            sb.append("owned\t")
                .append(entry.getKey())
                .append('\t')
                .append(entry.getValue().toLowerCase(Locale.ROOT))
                .append('\n');
        }
        atomicWriteUtf8(stateFile, sb.toString());
    }

    private static void validateDirectoryEntry(String rawName) throws IOException {
        String trimmed = rawName.endsWith("/") ? rawName.substring(0, rawName.length() - 1) : rawName;
        if (!"plugins".equals(trimmed) && !"core".equals(trimmed)) {
            throw new IOException("Disallowed directory entry in modpack archive: " + rawName);
        }
    }

    public static String validateOwnedRelativePath(String rawPath) throws IOException {
        if (rawPath == null) {
            throw new IOException("Null path in modpack.");
        }
        String path = rawPath.trim();
        if (path.isEmpty() || path.startsWith("/") || path.endsWith("/") || path.indexOf('\\') >= 0) {
            throw new IOException("Invalid relative path in modpack: " + rawPath);
        }
        if ("modpack.json".equals(path)) {
            return path;
        }
        if (!isAllowedOwnedSubpath(path)) {
            throw new IOException("Disallowed path in modpack: " + rawPath);
        }
        return path;
    }

    private static boolean isAllowedOwnedSubpath(String path) {
        if (path == null || path.indexOf('\\') >= 0) {
            return false;
        }
        String[] segments = path.split("/");
        if (segments.length != 2) {
            return false;
        }
        String dir = segments[0];
        String name = segments[1];
        if (!"plugins".equals(dir) && !"core".equals(dir)) {
            return false;
        }
        if (name.isEmpty() || ".".equals(name) || "..".equals(name)) {
            return false;
        }
        for (int i = 0; i < name.length(); i++) {
            char ch = name.charAt(i);
            boolean ok = (ch >= 'a' && ch <= 'z')
                || (ch >= 'A' && ch <= 'Z')
                || (ch >= '0' && ch <= '9')
                || ch == '.'
                || ch == '_'
                || ch == '-';
            if (!ok) {
                return false;
            }
        }
        return true;
    }

    private static String requireSha256(String value, String label) throws IOException {
        String trimmed = value == null ? "" : value.trim().toLowerCase(Locale.ROOT);
        if (!isSha256(trimmed)) {
            throw new IOException("Invalid SHA-256 for " + label + ": " + value);
        }
        return trimmed;
    }

    private static boolean isSha256(String value) {
        if (value == null || value.length() != 64) {
            return false;
        }
        for (int i = 0; i < 64; i++) {
            char c = value.charAt(i);
            boolean hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!hex) {
                return false;
            }
        }
        return true;
    }

    public static String sha256File(File file) throws IOException {
        MessageDigest digest = newSha256();
        try (FileInputStream in = new FileInputStream(file)) {
            byte[] buffer = new byte[16 * 1024];
            int read;
            while ((read = in.read(buffer)) != -1) {
                digest.update(buffer, 0, read);
            }
        }
        return toHex(digest.digest());
    }

    private static MessageDigest newSha256() throws IOException {
        try {
            return MessageDigest.getInstance("SHA-256");
        } catch (Exception ex) {
            throw new IOException("SHA-256 unavailable", ex);
        }
    }

    private static String toHex(byte[] bytes) {
        StringBuilder sb = new StringBuilder(bytes.length * 2);
        for (byte b : bytes) {
            sb.append(Character.forDigit((b >> 4) & 0xF, 16));
            sb.append(Character.forDigit(b & 0xF, 16));
        }
        return sb.toString();
    }

    private static String readUtf8(File file) throws IOException {
        byte[] bytes = Files.readAllBytes(file.toPath());
        return new String(bytes, StandardCharsets.UTF_8);
    }

    private static void ensureDir(File dir) throws IOException {
        if (dir == null) {
            return;
        }
        if (dir.isDirectory()) {
            return;
        }
        if (!dir.mkdirs() && !dir.isDirectory()) {
            throw new IOException("Unable to create directory: " + dir.getAbsolutePath());
        }
    }

    private static void deleteRecursively(File file) {
        if (file == null || !file.exists()) {
            return;
        }
        if (file.isDirectory()) {
            File[] children = file.listFiles();
            if (children != null) {
                for (File child : children) {
                    deleteRecursively(child);
                }
            }
        }
        file.delete();
    }

    private static void notifyProgress(ProgressListener listener, String step, int percent) {
        if (listener != null) {
            listener.onProgress(step, percent);
        }
    }
}
