package com.bepinex.android.bpp;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.util.Enumeration;
import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;
import org.json.JSONObject;

/**
 * Upstream BazaarPlusPlus's published Android Managed Payload, fetched by the launcher because
 * the game process's CoreCLR cannot make TLS connections. It reads upstream's release manifest,
 * and when the release is newer than the installed payload and built for the installed game and
 * the same non-managed payload, downloads it, checks the manifest's size and SHA-256, and stages
 * it under the Data Root's {@code Updates/<version>}. The BepInEx bridge verifies the signature
 * and the binding there, and upstream's Updater swaps it in on the next game start
 * (android-port/upstream-host/README.md).
 */
public final class BppOfficialPayload {
    public static final String MANIFEST_URL =
        "https://cdn.bazaarplusplus.com/installer/latest/mod/android-aarch64.json";
    static final String DOWNLOAD_PREFIX = "https://cdn.bazaarplusplus.com/installer/";
    static final String PLATFORM = "android-aarch64";
    static final String DATA_ROOT = "BazaarPlusPlusV6";
    static final String UPDATES = "Updates";
    /** Shared with BazaarPlusPlus.OfficialPayload.RejectedVersions. */
    static final String REJECTED_FILE = "bpp-official-payload.rejected";
    static final String INSTALLED_MANIFEST = "MelonLoader/Mods/BazaarPlusPlus.managed-manifest.txt";
    static final String VERSION_FILE = "MelonLoader/Mods/BazaarPlusPlus.version";

    private static final long MAX_PAYLOAD_BYTES = 64L * 1024 * 1024;
    private static final int MAX_MANIFEST_BYTES = 1024 * 1024;
    private static final Pattern VERSION =
        Pattern.compile("^(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})");
    private static final Pattern SHA256 = Pattern.compile("^[0-9a-f]{64}$");

    private BppOfficialPayload() { }

    /** The release manifest's Android Managed Payload. */
    public static final class Release {
        public final String version;
        public final String url;
        public final long size;
        public final String sha256;
        public final String gameVersion;
        public final String nonManagedPayloadDigest;
        public final int historyDatabaseUserVersion;

        Release(String version, String url, long size, String sha256, String gameVersion,
                String nonManagedPayloadDigest, int historyDatabaseUserVersion) {
            this.version = version;
            this.url = url;
            this.size = size;
            this.sha256 = sha256;
            this.gameVersion = gameVersion;
            this.nonManagedPayloadDigest = nonManagedPayloadDigest;
            this.historyDatabaseUserVersion = historyDatabaseUserVersion;
        }
    }

    public static Release parseRelease(String json) throws IOException {
        try {
            JSONObject root = new JSONObject(json);
            JSONObject platform = root.getJSONObject("downloads").getJSONObject(PLATFORM);
            JSONObject payload = platform.getJSONObject("managedPayload");
            String version = root.getString("version");
            String url = payload.getString("url");
            long size = payload.getLong("size");
            String sha256 = payload.getString("sha256").toLowerCase(Locale.ROOT);
            String digest = platform.getString("nonManagedPayloadDigest").toLowerCase(Locale.ROOT);
            if (!isVersion(version) || version.split("\\.").length != 3)
                throw new IOException("Unsupported payload version");
            // Only upstream's own CDN, never a URL the manifest could point anywhere else.
            if (!url.startsWith(DOWNLOAD_PREFIX) || url.contains("..") || url.contains("?"))
                throw new IOException("Unexpected payload URL");
            if (size <= 0 || size > MAX_PAYLOAD_BYTES) throw new IOException("Unexpected payload size");
            if (!SHA256.matcher(sha256).matches() || !SHA256.matcher(digest).matches())
                throw new IOException("Unexpected payload digest");
            return new Release(version, url, size, sha256,
                platform.getJSONObject("game").getString("gameVersion"), digest,
                platform.getInt("historyDatabaseUserVersion"));
        } catch (org.json.JSONException ex) {
            throw new IOException("Unreadable release manifest", ex);
        }
    }

    /** Compares the leading {@code major.minor.patch}, as upstream's ManagedPayloadVersion does. */
    static int compareVersions(String left, String right) {
        long[] a = parseVersion(left);
        long[] b = parseVersion(right);
        for (int i = 0; i < 3; i++) {
            int result = Long.compare(a[i], b[i]);
            if (result != 0) return result;
        }
        return 0;
    }

    private static boolean isVersion(String text) {
        return text != null && VERSION.matcher(text.trim()).find();
    }

    private static long[] parseVersion(String text) {
        Matcher match = VERSION.matcher(text == null ? "" : text.trim());
        if (!match.find()) throw new IllegalArgumentException("Unsupported payload version");
        return new long[] {
            Long.parseLong(match.group(1)), Long.parseLong(match.group(2)), Long.parseLong(match.group(3))
        };
    }

    /** The installed payload's version: its manifest's, else its version file's; null if none. */
    static String installedVersion(File installRoot) throws IOException {
        String manifestVersion = installedManifestField(installRoot, "version");
        if (manifestVersion != null) return manifestVersion;
        File versionFile = new File(installRoot, VERSION_FILE);
        if (!versionFile.isFile()) return null;
        String text = new String(Files.readAllBytes(versionFile.toPath()), StandardCharsets.UTF_8).trim();
        return isVersion(text) ? text : null;
    }

    private static String installedManifestField(File installRoot, String key) throws IOException {
        File manifest = new File(installRoot, INSTALLED_MANIFEST);
        if (!manifest.isFile()) return null;
        for (String line : new String(Files.readAllBytes(manifest.toPath()), StandardCharsets.UTF_8).split("\n")) {
            if (line.startsWith(key + " ")) return line.substring(key.length() + 1).trim();
        }
        return null;
    }

    static boolean isRejected(File installRoot, String version) throws IOException {
        File rejected = new File(new File(installRoot, DATA_ROOT), REJECTED_FILE);
        if (!rejected.isFile()) return false;
        for (String line : new String(Files.readAllBytes(rejected.toPath()), StandardCharsets.UTF_8).split("\n")) {
            if (line.split(" ", 2)[0].equals(version)) return true;
        }
        return false;
    }

    /** Why this release is not staged, or null to stage it. */
    static String skipReason(Release release, File installRoot, String gameVersion) throws IOException {
        if (!release.gameVersion.equals(gameVersion)) return "game_version";
        String installed = installedVersion(installRoot);
        if (installed != null && compareVersions(release.version, installed) <= 0) return "up_to_date";
        if (isRejected(installRoot, release.version)) return "rejected";
        if (stagedDirectory(installRoot, release.version).isDirectory()) return "staged";
        // Upstream's Updater refuses these too; checking here saves the download.
        String digest = installedManifestField(installRoot, "non-managed-payload-digest");
        if (digest != null && !digest.equals(release.nonManagedPayloadDigest)) return "non_managed_payload";
        String history = installedManifestField(installRoot, "history-database-user-version");
        if (history != null && !history.equals(String.valueOf(release.historyDatabaseUserVersion)))
            return "history_database";
        return null;
    }

    static File stagedDirectory(File installRoot, String version) {
        return new File(new File(new File(installRoot, DATA_ROOT), UPDATES), version);
    }

    /**
     * Extracts a payload zip as {@code Updates/<version>}: the manifest, its signature, and files
     * under {@code files/} that upstream's MelonLoader host may write. Anything else refuses the
     * whole zip. Published only once complete, by renaming the partial directory.
     */
    static void stage(File zip, File installRoot, String version) throws IOException {
        File target = stagedDirectory(installRoot, version);
        File partial = new File(target.getParentFile(), "." + version + ".partial");
        deleteRecursively(partial);
        if (!partial.mkdirs()) throw new IOException("Cannot create " + partial);
        try (ZipFile archive = new ZipFile(zip)) {
            Enumeration<? extends ZipEntry> entries = archive.entries();
            while (entries.hasMoreElements()) {
                ZipEntry entry = entries.nextElement();
                if (entry.isDirectory()) continue;
                String name = entry.getName();
                if (!isStagedEntry(name)) throw new IOException("Unexpected payload entry " + name);
                File destination = new File(partial, name);
                File parent = destination.getParentFile();
                if (parent != null && !parent.isDirectory() && !parent.mkdirs())
                    throw new IOException("Cannot create " + parent);
                try (InputStream input = archive.getInputStream(entry);
                     OutputStream output = new FileOutputStream(destination)) {
                    copy(input, output, MAX_PAYLOAD_BYTES, null);
                }
            }
            if (!new File(partial, "manifest.txt").isFile() || !new File(partial, "manifest.txt.minisig").isFile())
                throw new IOException("Payload manifest missing");
            if (!partial.renameTo(target)) throw new IOException("Cannot publish " + target);
        } catch (IOException ex) {
            deleteRecursively(partial);
            throw ex;
        }
    }

    static boolean isStagedEntry(String name) {
        if ("manifest.txt".equals(name) || "manifest.txt.minisig".equals(name)) return true;
        for (String directory : new String[] {"files/MelonLoader/Mods/", "files/MelonLoader/UserLibs/"}) {
            if (!name.startsWith(directory)) continue;
            String file = name.substring(directory.length());
            return !file.isEmpty() && !".".equals(file) && !"..".equals(file)
                && file.indexOf('/') < 0 && file.indexOf('\\') < 0;
        }
        return false;
    }

    /**
     * One check: fetch the manifest, stage the release when it applies, and say what happened.
     * Runs off the UI thread; never throws for an ordinary skip.
     */
    public static String check(File installRoot, String gameVersion) throws Exception {
        Release release = parseRelease(new String(fetch(MANIFEST_URL, MAX_MANIFEST_BYTES, null), StandardCharsets.UTF_8));
        String skip = skipReason(release, installRoot, gameVersion);
        if (skip != null) return "skipped version=" + release.version + " reason=" + skip;

        File updates = stagedDirectory(installRoot, release.version).getParentFile();
        if (!updates.isDirectory() && !updates.mkdirs()) throw new IOException("Cannot create " + updates);
        File download = new File(updates, "." + release.version + ".zip");
        try {
            MessageDigest sha = MessageDigest.getInstance("SHA-256");
            try (OutputStream output = new FileOutputStream(download)) {
                long written = copy(openStream(release.url), output, release.size, sha);
                if (written != release.size) throw new IOException("Payload size mismatch");
            }
            if (!toHex(sha.digest()).equals(release.sha256)) throw new IOException("Payload SHA-256 mismatch");
            stage(download, installRoot, release.version);
        } finally {
            if (download.exists() && !download.delete()) download.deleteOnExit();
        }
        return "staged version=" + release.version;
    }

    private static byte[] fetch(String url, int limit, MessageDigest digest) throws IOException {
        try (InputStream input = openStream(url); ByteArrayOutputStream output = new ByteArrayOutputStream()) {
            copy(input, output, limit, digest);
            return output.toByteArray();
        }
    }

    private static InputStream openStream(String url) throws IOException {
        HttpURLConnection connection = (HttpURLConnection) new URL(url).openConnection();
        connection.setConnectTimeout(8000);
        connection.setReadTimeout(15000);
        connection.setInstanceFollowRedirects(false);
        connection.setRequestProperty("User-Agent", "BazaarPlusPlus-Android-Launcher");
        int status = connection.getResponseCode();
        if (status != 200) {
            connection.disconnect();
            throw new IOException("HTTP " + status + " for " + url);
        }
        return connection.getInputStream();
    }

    private static long copy(InputStream input, OutputStream output, long limit, MessageDigest digest)
            throws IOException {
        try (InputStream in = input) {
            byte[] buffer = new byte[64 * 1024];
            long total = 0;
            int length;
            while ((length = in.read(buffer)) != -1) {
                if (Thread.currentThread().isInterrupted()) throw new IOException("Cancelled");
                total += length;
                if (total > limit) throw new IOException("Payload larger than expected");
                if (digest != null) digest.update(buffer, 0, length);
                output.write(buffer, 0, length);
            }
            return total;
        }
    }

    private static String toHex(byte[] bytes) {
        StringBuilder builder = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) builder.append(String.format(Locale.ROOT, "%02x", value));
        return builder.toString();
    }

    private static void deleteRecursively(File file) {
        if (!file.exists()) return;
        File[] children = file.listFiles();
        if (children != null) for (File child : children) deleteRecursively(child);
        if (!file.delete()) file.deleteOnExit();
    }
}
