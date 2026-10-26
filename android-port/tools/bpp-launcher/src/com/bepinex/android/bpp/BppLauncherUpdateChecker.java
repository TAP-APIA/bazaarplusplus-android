package com.bepinex.android.bpp;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import org.json.JSONArray;
import org.json.JSONObject;

/** Read-only update discovery. Installation remains an explicit user action in Android. */
public final class BppLauncherUpdateChecker {
    public static final String RELEASES_URL =
        "https://github.com/TAP-APIA/bazaarplusplus-android/releases";
    private static final String API_URL =
        "https://api.github.com/repos/TAP-APIA/bazaarplusplus-android/releases?per_page=100";
    private static final String APK_NAME = "BepInExLauncher-bpp-android.apk";
    private static final Pattern VERSION = Pattern.compile(
        "^(?:android-v)?([0-9]{1,9})\\.([0-9]{1,9})\\.([0-9]{1,9})(?:-preview\\.([0-9]{1,9}))?$");
    private static final int MAX_RESPONSE_BYTES = 4 * 1024 * 1024;

    private BppLauncherUpdateChecker() { }

    public static final class Update {
        public final String version;
        public final String releaseUrl;

        private Update(String tag) {
            version = tag.substring("android-v".length());
            // Construct from a validated tag, never navigate to an arbitrary URL in release text.
            releaseUrl = RELEASES_URL + "/tag/" + tag;
        }
    }

    public static int compareVersions(String left, String right) {
        long[] a = parseVersion(left);
        long[] b = parseVersion(right);
        for (int i = 0; i < a.length; i++) {
            int result = Long.compare(a[i], b[i]);
            if (result != 0) return result;
        }
        return 0;
    }

    private static long[] parseVersion(String text) {
        Matcher match = VERSION.matcher(text == null ? "" : text);
        if (!match.matches()) throw new IllegalArgumentException("Unsupported launcher version");
        return new long[] {
            Long.parseLong(match.group(1)), Long.parseLong(match.group(2)),
            Long.parseLong(match.group(3)),
            match.group(4) == null ? Long.MAX_VALUE : Long.parseLong(match.group(4))
        };
    }

    /** Includes prereleases, ignores drafts/unrelated tags/releases without an uploaded APK. */
    public static Update selectUpdate(String json, String currentVersion) throws Exception {
        parseVersion(currentVersion);
        JSONArray releases = new JSONArray(json);
        Update newest = null;
        for (int i = 0; i < releases.length(); i++) {
            JSONObject release = releases.getJSONObject(i);
            if (release.optBoolean("draft", true)) continue;
            String tag = release.optString("tag_name", "");
            if (!tag.startsWith("android-v") || !VERSION.matcher(tag).matches()) continue;
            if (compareVersions(tag, currentVersion) <= 0) continue;
            JSONArray assets = release.optJSONArray("assets");
            if (assets == null) continue;
            boolean hasApk = false;
            for (int j = 0; j < assets.length(); j++) {
                JSONObject asset = assets.getJSONObject(j);
                String expectedUrl = RELEASES_URL + "/download/" + tag + "/" + APK_NAME;
                if (APK_NAME.equals(asset.optString("name"))
                    && "uploaded".equals(asset.optString("state"))
                    && asset.optLong("size", 0) > 0
                    && expectedUrl.equals(asset.optString("browser_download_url"))) {
                    hasApk = true;
                    break;
                }
            }
            if (hasApk && (newest == null || compareVersions(tag, newest.version) > 0)) {
                newest = new Update(tag);
            }
        }
        return newest;
    }

    public static Update check(String currentVersion) throws Exception {
        HttpURLConnection connection = (HttpURLConnection) new URL(API_URL).openConnection();
        connection.setConnectTimeout(8000);
        connection.setReadTimeout(8000);
        connection.setInstanceFollowRedirects(false);
        connection.setRequestProperty("Accept", "application/vnd.github+json");
        connection.setRequestProperty("X-GitHub-Api-Version", "2022-11-28");
        connection.setRequestProperty("User-Agent", "BazaarPlusPlus-Android-Launcher");
        try {
            int status = connection.getResponseCode();
            if (status == 403 || status == 429) throw new IOException("GitHub 请求受限，请稍后重试");
            if (status != 200) throw new IOException("GitHub HTTP " + status);
            try (InputStream input = connection.getInputStream();
                 ByteArrayOutputStream output = new ByteArrayOutputStream()) {
                byte[] buffer = new byte[8192];
                int length;
                while ((length = input.read(buffer)) != -1) {
                    if (Thread.currentThread().isInterrupted()) throw new IOException("检查已取消");
                    if (output.size() + length > MAX_RESPONSE_BYTES) throw new IOException("更新响应过大");
                    output.write(buffer, 0, length);
                }
                return selectUpdate(new String(output.toByteArray(), StandardCharsets.UTF_8), currentVersion);
            }
        } finally {
            connection.disconnect();
        }
    }
}
