package com.bepinex.android.bpp;

import org.json.JSONArray;
import org.json.JSONObject;

/** Host tests require an org.json JVM implementation on the runtime classpath. */
public final class BppLauncherUpdateCheckerTests {
    private static int assertions;

    public static void main(String[] args) throws Exception {
        assertThat(BppLauncherUpdateChecker.compareVersions("6.0.0-preview.10", "6.0.0-preview.9") > 0);
        assertThat(BppLauncherUpdateChecker.compareVersions("6.0.0-preview.100", "6.0.0-preview.38") > 0);
        assertThat(BppLauncherUpdateChecker.compareVersions("6.0.0", "6.0.0-preview.999") > 0);
        assertThat(BppLauncherUpdateChecker.compareVersions("6.1.0-preview.1", "6.0.0") > 0);
        assertThat(BppLauncherUpdateChecker.compareVersions("7.0.0-preview.1", "6.99.99") > 0);
        assertThat(BppLauncherUpdateChecker.compareVersions("android-v6.0.0-preview.37", "6.0.0-preview.37") == 0);
        for (String invalid : new String[] {"", "1.0.0-ci.99", "6.0", "6.0.0-preview.-1", "6.0.0-preview.9999999999", "6.0.0/../../evil", "6.0.0\n"}) {
            boolean threw = false;
            try { BppLauncherUpdateChecker.compareVersions(invalid, "6.0.0"); }
            catch (IllegalArgumentException expected) { threw = true; }
            assertThat(threw);
        }
        JSONObject v38 = release("android-v6.0.0-preview.38");
        assertThat(select("6.0.0-preview.37", v38).version.equals("6.0.0-preview.38"));
        assertThat(select("6.0.0-preview.38", v38) == null);
        assertThat(select("6.0.0-preview.39", v38) == null);
        assertThat(select("6.0.0", v38) == null);
        assertThat(select("6.0.0-preview.37", release("android-v6.0.0")).version.equals("6.0.0"));
        assertThat(select("6.0.0-preview.9", v38, release("android-v6.0.0-preview.10"))
            .version.equals("6.0.0-preview.38"));
        assertThat(select("6.0.0-preview.9", release("android-v6.0.0-preview.10"), v38)
            .version.equals("6.0.0-preview.38"));
        assertThat(select("6.0.0-preview.37", release("android-v6.0.0-preview.39").put("draft", true)) == null);
        assertThat(select("6.0.0-preview.37", release("v9.0.0")) == null);
        assertThat(select("6.0.0-preview.37", release("android-v6.0.0-preview.39").put("assets", new JSONArray())) == null);
        JSONObject pending = release("android-v6.0.0-preview.39");
        pending.getJSONArray("assets").getJSONObject(0).put("state", "starter");
        assertThat(select("6.0.0-preview.37", pending) == null);
        JSONObject empty = release("android-v6.0.0-preview.39");
        empty.getJSONArray("assets").getJSONObject(0).put("size", 0);
        assertThat(select("6.0.0-preview.37", empty) == null);
        JSONObject wrongHost = release("android-v6.0.0-preview.39");
        wrongHost.getJSONArray("assets").getJSONObject(0).put("browser_download_url", "https://evil.example/app.apk");
        assertThat(select("6.0.0-preview.37", wrongHost) == null);
        assertThat(select("6.0.0-preview.37", pending, v38).version.equals("6.0.0-preview.38"));
        assertThat(select("6.0.0-preview.37") == null);
        assertThat(select("6.0.0-preview.37", v38).releaseUrl.equals(
            BppLauncherUpdateChecker.RELEASES_URL + "/tag/android-v6.0.0-preview.38"));
        assertThat(select("6.0.0-preview.37", v38).apkUrl.equals(
            BppLauncherUpdateChecker.RELEASES_URL + "/download/android-v6.0.0-preview.38/BepInExLauncher-bpp-android.apk"));
        assertThat(select("6.0.0-preview.37", v38).apkSize == 100);
        boolean malformedFailed = false;
        try { BppLauncherUpdateChecker.selectUpdate("{broken", "6.0.0-preview.37"); }
        catch (Exception expected) { malformedFailed = true; }
        assertThat(malformedFailed);
        System.out.println("Launcher update tests passed: " + assertions + " assertions");
        if (args.length > 0 && "--live".equals(args[0])) {
            BppLauncherUpdateChecker.Update actual = BppLauncherUpdateChecker.check("6.0.0-preview.1");
            if (actual == null) throw new AssertionError("Expected a published Android release");
            System.out.println("Live GitHub release: " + actual.version + " " + actual.releaseUrl);
        }
    }

    private static BppLauncherUpdateChecker.Update select(String current, JSONObject... releases) throws Exception {
        JSONArray array = new JSONArray();
        for (JSONObject release : releases) array.put(release);
        return BppLauncherUpdateChecker.selectUpdate(array.toString(), current);
    }

    private static JSONObject release(String tag) throws Exception {
        JSONObject asset = new JSONObject().put("name", "BepInExLauncher-bpp-android.apk")
            .put("state", "uploaded").put("size", 100)
            .put("browser_download_url", BppLauncherUpdateChecker.RELEASES_URL + "/download/" + tag + "/BepInExLauncher-bpp-android.apk");
        return new JSONObject().put("draft", false).put("prerelease", true)
            .put("tag_name", tag).put("assets", new JSONArray().put(asset));
    }

    private static void assertThat(boolean condition) {
        assertions++;
        if (!condition) throw new AssertionError("Assertion " + assertions + " failed");
    }
}
