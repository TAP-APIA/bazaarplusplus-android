package com.bepinex.android.bpp;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;
import org.json.JSONObject;

/** Host tests require an org.json JVM implementation on the runtime classpath. */
public final class BppOfficialPayloadTests {
    private static final String DIGEST = "f41fcb027911e235fb48424eab16fd2698266cd4ab9ecba5605aae19646f7ae1";
    private static final String GAME = "1.0.12825-production-android-d9601ded";
    private static int assertions;

    public static void main(String[] args) throws Exception {
        BppOfficialPayload.Release release = BppOfficialPayload.parseRelease(manifest("6.0.3").toString());
        assertThat(release.version.equals("6.0.3"));
        assertThat(release.url.startsWith(BppOfficialPayload.DOWNLOAD_PREFIX));
        assertThat(release.size == 2845338);
        assertThat(release.gameVersion.equals(GAME));
        assertThat(release.nonManagedPayloadDigest.equals(DIGEST));
        assertThat(release.historyDatabaseUserVersion == 1);

        JSONObject foreign = manifest("6.0.3");
        payload(foreign).put("url", "https://example.com/installer/x.zip");
        assertThrows(() -> BppOfficialPayload.parseRelease(foreign.toString()));
        JSONObject climbing = manifest("6.0.3");
        payload(climbing).put("url", BppOfficialPayload.DOWNLOAD_PREFIX + "../x.zip");
        assertThrows(() -> BppOfficialPayload.parseRelease(climbing.toString()));
        JSONObject badSha = manifest("6.0.3");
        payload(badSha).put("sha256", "abc");
        assertThrows(() -> BppOfficialPayload.parseRelease(badSha.toString()));
        assertThrows(() -> BppOfficialPayload.parseRelease(manifest("6.0").toString()));

        assertThat(BppOfficialPayload.compareVersions("6.0.2.prod", "6.0.2") == 0);
        assertThat(BppOfficialPayload.compareVersions("6.0.10", "6.0.9") > 0);
        assertThat(BppOfficialPayload.compareVersions("6.1.0", "6.0.99") > 0);

        File root = Files.createTempDirectory("bpp-official-payload").toFile();
        try {
            File nothing = new File(root, "nothing");
            assertThat(BppOfficialPayload.skipReason(release, nothing, GAME) == null);
            assertThat("game_version".equals(BppOfficialPayload.skipReason(release, nothing, "1.0.1")));

            File installed = new File(root, "installed");
            write(new File(installed, BppOfficialPayload.VERSION_FILE), "6.0.3.prod");
            assertThat("up_to_date".equals(BppOfficialPayload.skipReason(release, installed, GAME)));
            write(new File(installed, BppOfficialPayload.VERSION_FILE), "6.0.2.prod");
            assertThat(BppOfficialPayload.skipReason(release, installed, GAME) == null);

            write(new File(installed, BppOfficialPayload.INSTALLED_MANIFEST),
                "bpp-managed-payload-manifest 1\nversion 6.0.2\nplatform android-aarch64\n"
                    + "history-database-user-version 1\nnon-managed-payload-digest " + "0".repeat(64) + "\n");
            assertThat("non_managed_payload".equals(BppOfficialPayload.skipReason(release, installed, GAME)));
            write(new File(installed, BppOfficialPayload.INSTALLED_MANIFEST),
                "bpp-managed-payload-manifest 1\nversion 6.0.2\nplatform android-aarch64\n"
                    + "history-database-user-version 2\nnon-managed-payload-digest " + DIGEST + "\n");
            assertThat("history_database".equals(BppOfficialPayload.skipReason(release, installed, GAME)));
            write(new File(installed, BppOfficialPayload.INSTALLED_MANIFEST),
                "bpp-managed-payload-manifest 1\nversion 6.0.2\nplatform android-aarch64\n"
                    + "history-database-user-version 1\nnon-managed-payload-digest " + DIGEST + "\n");
            assertThat(BppOfficialPayload.skipReason(release, installed, GAME) == null);

            write(new File(installed, BppOfficialPayload.DATA_ROOT + "/" + BppOfficialPayload.REJECTED_FILE),
                "6.0.1 launch_unconfirmed\n6.0.3 member_missing:x\n");
            assertThat("rejected".equals(BppOfficialPayload.skipReason(release, installed, GAME)));

            File staging = new File(root, "staging");
            BppOfficialPayload.stage(zip(root, "good.zip", "files/MelonLoader/Mods/BazaarPlusPlus.Android.dll",
                "files/MelonLoader/UserLibs/MessagePack.dll"), staging, "6.0.3");
            File staged = BppOfficialPayload.stagedDirectory(staging, "6.0.3");
            assertThat(new File(staged, "manifest.txt").isFile());
            assertThat(new File(staged, "manifest.txt.minisig").isFile());
            assertThat(new File(staged, "files/MelonLoader/Mods/BazaarPlusPlus.Android.dll").isFile());
            assertThat("staged".equals(BppOfficialPayload.skipReason(release, staging, GAME)));

            for (String evil : new String[] {
                    "files/BepInEx/plugins/Evil.dll", "files/MelonLoader/Mods/../../Evil.dll",
                    "files/MelonLoader/Mods/sub/Evil.dll", "../Evil.dll", "Evil.txt"}) {
                File refused = new File(root, "refused");
                File evilZip = zip(root, "evil.zip", evil);
                assertThrows(() -> BppOfficialPayload.stage(evilZip, refused, "6.0.3"));
                assertThat(!BppOfficialPayload.stagedDirectory(refused, "6.0.3").exists());
                assertThat(!new File(BppOfficialPayload.stagedDirectory(refused, "6.0.3").getParentFile(), ".6.0.3.partial").exists());
            }
        } finally {
            deleteRecursively(root);
        }
        System.out.println("BppOfficialPayloadTests OK assertions=" + assertions);
    }

    private static JSONObject manifest(String version) {
        return new JSONObject()
            .put("version", version)
            .put("downloads", new JSONObject().put("android-aarch64", new JSONObject()
                .put("managedPayload", new JSONObject()
                    .put("url", BppOfficialPayload.DOWNLOAD_PREFIX + version + "/android-aarch64/managed/p.zip")
                    .put("size", 2845338)
                    .put("sha256", "fde6b73668c07c7e6f56be59e8a124369482f7aeb6b5da3cc3cd04d4f8c7f017"))
                .put("nonManagedPayloadDigest", DIGEST)
                .put("historyDatabaseUserVersion", 1)
                .put("game", new JSONObject().put("gameVersion", GAME))));
    }

    private static JSONObject payload(JSONObject manifest) {
        return manifest.getJSONObject("downloads").getJSONObject("android-aarch64").getJSONObject("managedPayload");
    }

    private static File zip(File directory, String name, String... files) throws IOException {
        File zip = new File(directory, name);
        try (ZipOutputStream output = new ZipOutputStream(new FileOutputStream(zip))) {
            for (String entry : new String[] {"manifest.txt", "manifest.txt.minisig"}) {
                output.putNextEntry(new ZipEntry(entry));
                output.write("x".getBytes(StandardCharsets.UTF_8));
                output.closeEntry();
            }
            for (String entry : files) {
                output.putNextEntry(new ZipEntry(entry));
                output.write(entry.getBytes(StandardCharsets.UTF_8));
                output.closeEntry();
            }
        }
        return zip;
    }

    private static void write(File file, String text) throws IOException {
        file.getParentFile().mkdirs();
        Files.write(file.toPath(), text.getBytes(StandardCharsets.UTF_8));
    }

    private static void deleteRecursively(File file) {
        File[] children = file.listFiles();
        if (children != null) for (File child : children) deleteRecursively(child);
        file.delete();
    }

    private interface Action { void run() throws Exception; }

    private static void assertThrows(Action action) throws Exception {
        boolean threw = false;
        try { action.run(); } catch (IOException expected) { threw = true; }
        assertThat(threw);
    }

    private static void assertThat(boolean condition) {
        assertions++;
        if (!condition) throw new AssertionError("assertion " + assertions + " failed");
    }
}
