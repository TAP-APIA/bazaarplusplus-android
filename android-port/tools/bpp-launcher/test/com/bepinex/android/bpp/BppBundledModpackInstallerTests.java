package com.bepinex.android.bpp;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

public final class BppBundledModpackInstallerTests {

    private static final String[] REQUIRED = new String[] {
        "plugins/BazaarPlusPlus.dll",
        "plugins/BazaarPlusPlus.Localization.dll",
        "plugins/BazaarPlusPlus.ModApi.dll",
        "plugins/BazaarPlusPlus.Storage.dll",
        "plugins/Bpp.Managed.MessagePack.dll",
        "plugins/Bpp.Managed.MessagePack.Annotations.dll",
        "plugins/Bpp.Managed.StringTools.dll",
        "plugins/Microsoft.Data.Sqlite.dll",
        "plugins/SQLitePCLRaw.core.dll",
        "plugins/SQLitePCLRaw.provider.e_sqlite3.dll",
        "plugins/SQLitePCLRaw.batteries_v2.dll",
        "plugins/libe_sqlite3.so",
        "core/BepInEx.Unity.IL2CPP.dll",
        "core/BepInEx.Core.dll",
        "core/Il2CppInterop.Runtime.dll",
        "core/Il2CppInterop.HarmonySupport.dll"
    };

    // The BepInEx bridge with upstream's published payload bundled (android-port/upstream-host).
    private static final String[] REQUIRED_UPSTREAM = new String[] {
        "plugins/BazaarPlusPlus.BepInExBridge.dll",
        "plugins/BazaarPlusPlus.OfficialPayload.dll",
        "plugins/BazaarPlusPlus.OfficialPayload.zip",
        "plugins/MelonLoader.dll",
        "plugins/libe_sqlite3.so",
        "core/BepInEx.Unity.IL2CPP.dll",
        "core/BepInEx.Core.dll",
        "core/Il2CppInterop.Runtime.dll",
        "core/Il2CppInterop.HarmonySupport.dll"
    };

    public static void main(String[] args) throws Exception {
        testFirstInstallAndConfigSeeding();
        testUpgradePreservesUserConfigHistoryAndMetadataAndRetiresOldOwnedOnly();
        testRepairFixesMissingOrCorruptedFile();
        testRejectsTraversalUnlistedAndCorruptArchivesWithoutMutatingTarget();
        testInterruptedInProgressStateRequiresRepair();
        testSwitchToUpstreamPayloadRetiresForkPlugin();
        testRejectsIncompletePluginPayload();
        System.out.println("PASS: all BppBundledModpackInstaller tests succeeded.");
    }

    private static void testFirstInstallAndConfigSeeding() throws Exception {
        File root = Files.createTempDirectory("bpp-installer-first").toFile();
        File gameRoot = new File(root, "game");
        File workDir = new File(root, "work");
        FixtureBundle bundle = createFixtureBundle("v1", null);
        byte[] bepZip = createSingleEntryZip("config/BepInEx.cfg", "[Caching]\nEnableAssemblyCache = true\n");

        BppBundledModpackInstaller.Manifest manifest = BppBundledModpackInstaller.parseManifest(
            new ByteArrayInputStream(bundle.manifestBytes)
        );
        BppBundledModpackInstaller.StatusReport initial = BppBundledModpackInstaller.inspectStatus(
            gameRoot,
            workDir,
            manifest,
            true
        );
        assertTrue(initial.kind == BppBundledModpackInstaller.StatusKind.NEEDS_INSTALL, "Expected NEEDS_INSTALL");

        BppBundledModpackInstaller.StatusReport installed = BppBundledModpackInstaller.installOrUpdate(
            gameRoot,
            workDir,
            () -> new ByteArrayInputStream(bundle.manifestBytes),
            () -> new ByteArrayInputStream(bundle.zipBytes),
            () -> new ByteArrayInputStream(bepZip),
            false,
            null
        );
        assertTrue(installed.isReady(), "Expected READY after first install");
        assertTrue(new File(gameRoot, "BepInEx/config/BepInEx.cfg").isFile(), "BepInEx.cfg must be seeded in runtime config");
        assertTrue(new File(gameRoot, "modpacks/bpp/config/BepInEx.cfg").isFile(), "BepInEx.cfg must be seeded in modpack config");
        assertTrue(new File(gameRoot, "BepInEx/plugins/BazaarPlusPlus.dll").isFile(), "Runtime plugin missing");
        assertTrue(new File(gameRoot, "modpacks/bpp/plugins/BazaarPlusPlus.dll").isFile(), "Modpack plugin missing");
    }

    private static void testUpgradePreservesUserConfigHistoryAndMetadataAndRetiresOldOwnedOnly() throws Exception {
        File root = Files.createTempDirectory("bpp-installer-upgrade").toFile();
        File gameRoot = new File(root, "game");
        File workDir = new File(root, "work");

        Map<String, String> v1Extras = new LinkedHashMap<String, String>();
        v1Extras.put("plugins/RetiredOwned.dll", "retired-v1");
        v1Extras.put("plugins/UserEditedOwned.dll", "owned-v1");
        FixtureBundle v1 = createFixtureBundle("v1", v1Extras);

        BppBundledModpackInstaller.installOrUpdate(
            gameRoot,
            workDir,
            () -> new ByteArrayInputStream(v1.manifestBytes),
            () -> new ByteArrayInputStream(v1.zipBytes),
            null,
            false,
            null
        );

        File userCfg = new File(gameRoot, "BepInEx/config/BazaarPlusPlus.cfg");
        userCfg.getParentFile().mkdirs();
        Files.write(userCfg.toPath(), "[TouchDock]\nAnchorX = 0.25\nAnchorY = 0.75\n".getBytes(StandardCharsets.UTF_8));

        File thirdPartyDll = new File(gameRoot, "BepInEx/plugins/ThirdPartyHelper.dll");
        Files.write(thirdPartyDll.toPath(), "third-party-keep".getBytes(StandardCharsets.UTF_8));

        File userEditedOwned = new File(gameRoot, "BepInEx/plugins/UserEditedOwned.dll");
        Files.write(userEditedOwned.toPath(), "user-modified-keep".getBytes(StandardCharsets.UTF_8));

        File customMeta = new File(gameRoot, "modpacks/bpp/modpack.json");
        Files.write(
            customMeta.toPath(),
            "{\"name\":\"bpp\",\"packageName\":\"com.tempogames.thebazaar\",\"createdAt\":12345,\"modCount\":99,\"mods\":{\"Custom.dll\":{\"enabled\":false}}}".getBytes(StandardCharsets.UTF_8)
        );

        FixtureBundle v2 = createFixtureBundle("v2-updated", null);
        BppBundledModpackInstaller.Manifest v2Manifest = BppBundledModpackInstaller.parseManifest(
            new ByteArrayInputStream(v2.manifestBytes)
        );
        BppBundledModpackInstaller.StatusReport beforeUpgrade = BppBundledModpackInstaller.inspectStatus(
            gameRoot,
            workDir,
            v2Manifest,
            true
        );
        assertTrue(beforeUpgrade.kind == BppBundledModpackInstaller.StatusKind.NEEDS_UPDATE, "Expected NEEDS_UPDATE");

        BppBundledModpackInstaller.StatusReport upgraded = BppBundledModpackInstaller.installOrUpdate(
            gameRoot,
            workDir,
            () -> new ByteArrayInputStream(v2.manifestBytes),
            () -> new ByteArrayInputStream(v2.zipBytes),
            null,
            false,
            null
        );
        assertTrue(upgraded.isReady(), "Expected READY after upgrade");

        String cfgText = new String(Files.readAllBytes(userCfg.toPath()), StandardCharsets.UTF_8);
        assertTrue(cfgText.contains("AnchorX = 0.25"), "User config must remain untouched");
        String syncedModpackCfg = new String(
            Files.readAllBytes(new File(gameRoot, "modpacks/bpp/config/BazaarPlusPlus.cfg").toPath()),
            StandardCharsets.UTF_8
        );
        assertTrue(syncedModpackCfg.contains("AnchorX = 0.25"), "User config must be additively synced to modpacks/bpp/config");
        assertTrue(thirdPartyDll.isFile(), "Third-party plugin must remain untouched");
        assertTrue(!new File(gameRoot, "BepInEx/plugins/RetiredOwned.dll").exists(), "Unmodified retired owned file should be removed");
        assertTrue(!new File(gameRoot, "modpacks/bpp/plugins/RetiredOwned.dll").exists(), "Unmodified retired owned file in modpack should be removed");
        assertTrue(userEditedOwned.isFile(), "Modified retired file must not be deleted");
        String metaText = new String(Files.readAllBytes(customMeta.toPath()), StandardCharsets.UTF_8);
        assertTrue(metaText.contains("\"Custom.dll\""), "Existing modpack metadata customizations must be preserved");
        assertTrue(metaText.contains("\"modCount\":11"), "modCount should update to current managed DLL count");
    }

    private static void testRepairFixesMissingOrCorruptedFile() throws Exception {
        File root = Files.createTempDirectory("bpp-installer-repair").toFile();
        File gameRoot = new File(root, "game");
        File workDir = new File(root, "work");
        FixtureBundle bundle = createFixtureBundle("v1", null);
        BppBundledModpackInstaller.Manifest manifest = BppBundledModpackInstaller.parseManifest(
            new ByteArrayInputStream(bundle.manifestBytes)
        );
        BppBundledModpackInstaller.installOrUpdate(
            gameRoot,
            workDir,
            () -> new ByteArrayInputStream(bundle.manifestBytes),
            () -> new ByteArrayInputStream(bundle.zipBytes),
            null,
            false,
            null
        );

        File runtimePlugin = new File(gameRoot, "BepInEx/plugins/BazaarPlusPlus.dll");
        Files.write(runtimePlugin.toPath(), "corrupted".getBytes(StandardCharsets.UTF_8));

        BppBundledModpackInstaller.StatusReport damaged = BppBundledModpackInstaller.inspectStatus(
            gameRoot,
            workDir,
            manifest,
            true
        );
        assertTrue(damaged.kind == BppBundledModpackInstaller.StatusKind.NEEDS_REPAIR, "Expected NEEDS_REPAIR for corrupted plugin");

        BppBundledModpackInstaller.StatusReport repaired = BppBundledModpackInstaller.installOrUpdate(
            gameRoot,
            workDir,
            () -> new ByteArrayInputStream(bundle.manifestBytes),
            () -> new ByteArrayInputStream(bundle.zipBytes),
            null,
            false,
            null
        );
        assertTrue(repaired.isReady(), "Expected READY after repair");
    }

    private static void testRejectsTraversalUnlistedAndCorruptArchivesWithoutMutatingTarget() throws Exception {
        File root = Files.createTempDirectory("bpp-installer-reject").toFile();
        File gameRoot = new File(root, "game");
        File workDir = new File(root, "work");
        FixtureBundle bundle = createFixtureBundle("v1", null);

        byte[] traversalZip = createSingleEntryZip("../escape.dll", "bad");
        assertRejected(() -> BppBundledModpackInstaller.installOrUpdate(
            gameRoot,
            workDir,
            () -> new ByteArrayInputStream(bundle.manifestBytes),
            () -> new ByteArrayInputStream(traversalZip),
            null,
            true,
            null
        ), "Traversal archive must be rejected");
        assertTrue(!new File(gameRoot, "BepInEx/plugins/BazaarPlusPlus.dll").exists(), "Rejected archive must not write target");

        byte[] configZip = createSingleEntryZip("config/BazaarPlusPlus.cfg", "bad");
        assertRejected(() -> BppBundledModpackInstaller.installOrUpdate(
            gameRoot,
            workDir,
            () -> new ByteArrayInputStream(bundle.manifestBytes),
            () -> new ByteArrayInputStream(configZip),
            null,
            true,
            null
        ), "Config entry in archive must be rejected");
    }

    private static void testInterruptedInProgressStateRequiresRepair() throws Exception {
        File root = Files.createTempDirectory("bpp-installer-interrupted").toFile();
        File gameRoot = new File(root, "game");
        File workDir = new File(root, "work");
        FixtureBundle bundle = createFixtureBundle("v1", null);
        BppBundledModpackInstaller.Manifest manifest = BppBundledModpackInstaller.parseManifest(
            new ByteArrayInputStream(bundle.manifestBytes)
        );
        BppBundledModpackInstaller.installOrUpdate(
            gameRoot,
            workDir,
            () -> new ByteArrayInputStream(bundle.manifestBytes),
            () -> new ByteArrayInputStream(bundle.zipBytes),
            null,
            false,
            null
        );
        File stateFile = new File(workDir, BppBundledModpackInstaller.STATE_FILE_NAME);
        Files.write(
            stateFile.toPath(),
            ("status\tin_progress\nbundle_id\t" + manifest.bundleId + "\n").getBytes(StandardCharsets.UTF_8)
        );
        BppBundledModpackInstaller.StatusReport status = BppBundledModpackInstaller.inspectStatus(
            gameRoot,
            workDir,
            manifest,
            true
        );
        assertTrue(status.kind == BppBundledModpackInstaller.StatusKind.NEEDS_REPAIR, "in_progress state must require repair");
    }

    private interface ThrowingRunnable {
        void run() throws Exception;
    }

    private static void assertRejected(ThrowingRunnable action, String message) {
        boolean rejected = false;
        try {
            action.run();
        } catch (Exception ex) {
            rejected = true;
        }
        assertTrue(rejected, message);
    }

    private static void assertTrue(boolean condition, String message) {
        if (!condition) {
            throw new IllegalStateException(message);
        }
    }

    private static final class FixtureBundle {
        final byte[] zipBytes;
        final byte[] manifestBytes;

        FixtureBundle(byte[] zipBytes, byte[] manifestBytes) {
            this.zipBytes = zipBytes;
            this.manifestBytes = manifestBytes;
        }
    }

    private static void testSwitchToUpstreamPayloadRetiresForkPlugin() throws Exception {
        File root = Files.createTempDirectory("bpp-installer-switch").toFile();
        File gameRoot = new File(root, "game");
        File workDir = new File(root, "work");
        FixtureBundle fork = createFixtureBundle("fork", null);
        BppBundledModpackInstaller.installOrUpdate(
            gameRoot,
            workDir,
            () -> new ByteArrayInputStream(fork.manifestBytes),
            () -> new ByteArrayInputStream(fork.zipBytes),
            null,
            false,
            null
        );
        File history = new File(gameRoot, "BazaarPlusPlusV6/bazaarplusplus.db");
        history.getParentFile().mkdirs();
        Files.write(history.toPath(), "history-keep".getBytes(StandardCharsets.UTF_8));

        FixtureBundle upstream = createFixtureBundle("upstream", null, REQUIRED_UPSTREAM);
        BppBundledModpackInstaller.StatusReport switched = BppBundledModpackInstaller.installOrUpdate(
            gameRoot,
            workDir,
            () -> new ByteArrayInputStream(upstream.manifestBytes),
            () -> new ByteArrayInputStream(upstream.zipBytes),
            null,
            false,
            null
        );
        assertTrue(switched.isReady(), "Expected READY after switching to the upstream payload");
        assertTrue(new File(gameRoot, "BepInEx/plugins/BazaarPlusPlus.BepInExBridge.dll").isFile(), "Bridge plugin missing");
        assertTrue(!new File(gameRoot, "BepInEx/plugins/BazaarPlusPlus.dll").exists(), "Fork plugin must be retired");
        assertTrue(!new File(gameRoot, "BepInEx/plugins/Bpp.Managed.MessagePack.dll").exists(), "Fork codec must be retired");
        assertTrue(!new File(gameRoot, "BepInEx/plugins/BazaarPlusPlus.Storage.dll").exists(),
            "Fork storage must be retired: the payload carries its own in MelonLoader/UserLibs");
        assertTrue(new File(gameRoot, "BepInEx/plugins/BazaarPlusPlus.OfficialPayload.zip").isFile(), "Bundled payload missing");
        assertTrue(history.isFile(), "History database must survive the switch");
    }

    private static void testRejectsIncompletePluginPayload() throws Exception {
        String[] partial = new String[REQUIRED_UPSTREAM.length - 1];
        int i = 0;
        for (String path : REQUIRED_UPSTREAM) {
            if (!path.equals("plugins/MelonLoader.dll")) {
                partial[i++] = path;
            }
        }
        FixtureBundle bundle = createFixtureBundle("partial", null, partial);
        try {
            BppBundledModpackInstaller.parseManifest(new ByteArrayInputStream(bundle.manifestBytes));
        } catch (IOException expected) {
            assertTrue(expected.getMessage().contains("no complete plugin payload"), "Unexpected error: " + expected.getMessage());
            return;
        }
        throw new AssertionError("A payload missing MelonLoader.dll must be rejected");
    }

    private static FixtureBundle createFixtureBundle(String tag, Map<String, String> extras) throws Exception {
        return createFixtureBundle(tag, extras, REQUIRED);
    }

    private static FixtureBundle createFixtureBundle(
        String tag,
        Map<String, String> extras,
        String[] required
    ) throws Exception {
        Map<String, byte[]> files = new LinkedHashMap<String, byte[]>();
        files.put(
            "modpack.json",
            "{\"name\":\"bpp\",\"packageName\":\"com.tempogames.thebazaar\",\"createdAt\":1,\"modCount\":11,\"createShortcut\":false}"
                .getBytes(StandardCharsets.UTF_8)
        );
        for (String path : required) {
            files.put(path, (path + ":" + tag).getBytes(StandardCharsets.UTF_8));
        }
        if (extras != null) {
            for (Map.Entry<String, String> entry : extras.entrySet()) {
                files.put(entry.getKey(), entry.getValue().getBytes(StandardCharsets.UTF_8));
            }
        }
        ByteArrayOutputStream zipBuffer = new ByteArrayOutputStream();
        try (ZipOutputStream zos = new ZipOutputStream(zipBuffer)) {
            for (Map.Entry<String, byte[]> entry : files.entrySet()) {
                zos.putNextEntry(new ZipEntry(entry.getKey()));
                zos.write(entry.getValue());
                zos.closeEntry();
            }
        }
        byte[] zipBytes = zipBuffer.toByteArray();
        String bundleId = sha256Hex(zipBytes);
        StringBuilder manifest = new StringBuilder();
        manifest.append("bundle_id\t").append(bundleId).append("\t6.0.0-").append(tag).append('\n');
        for (Map.Entry<String, byte[]> entry : files.entrySet()) {
            manifest.append("file\t")
                .append(entry.getKey())
                .append('\t')
                .append(entry.getValue().length)
                .append('\t')
                .append(sha256Hex(entry.getValue()))
                .append('\n');
        }
        return new FixtureBundle(zipBytes, manifest.toString().getBytes(StandardCharsets.UTF_8));
    }

    private static byte[] createSingleEntryZip(String name, String content) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        try (ZipOutputStream zos = new ZipOutputStream(out)) {
            zos.putNextEntry(new ZipEntry(name));
            zos.write(content.getBytes(StandardCharsets.UTF_8));
            zos.closeEntry();
        }
        return out.toByteArray();
    }

    private static String sha256Hex(byte[] bytes) throws Exception {
        byte[] digest = MessageDigest.getInstance("SHA-256").digest(bytes);
        StringBuilder sb = new StringBuilder(digest.length * 2);
        for (byte b : digest) {
            sb.append(Character.forDigit((b >> 4) & 0xF, 16));
            sb.append(Character.forDigit(b & 0xF, 16));
        }
        return sb.toString();
    }
}
