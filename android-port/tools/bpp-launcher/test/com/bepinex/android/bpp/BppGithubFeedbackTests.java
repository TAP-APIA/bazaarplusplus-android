package com.bepinex.android.bpp;

import java.net.URLDecoder;

public final class BppGithubFeedbackTests {

    public static void main(String[] args) throws Exception {
        testUrlContainsTemplateAndBasename();
        testLongInputStillClamped();
        System.out.println("PASS: all BppGithubFeedback tests succeeded.");
    }

    private static void testUrlContainsTemplateAndBasename() throws Exception {
        String fakePath = "/data/user/0/com.bepinex.android.bpp/cache/BPP-diagnostics-20261027-123456.zip";
        String url = BppGithubFeedback.buildIssueUrl(
            "6.0.0-preview.44",
            "6.0.0-preview.44",
            "abc123",
            "Google",
            "Pixel 8",
            "15",
            35,
            "arm64-v8a, armeabi-v7a",
            fakePath
        );
        assertTrue(url.startsWith(BppGithubFeedback.ISSUES_NEW_BASE), url);
        assertTrue(url.contains("title=" + BppGithubFeedback.encodeQuery(BppGithubFeedback.buildTitle())), url);
        assertTrue(!url.contains("+"), "URL must encode spaces as %20");
        assertTrue(url.length() <= BppGithubFeedback.MAX_URL_CHARS, "URL too long: " + url.length());

        String decoded = URLDecoder.decode(url, "UTF-8");
        assertTrue(decoded.contains("## 问题描述"), decoded);
        assertTrue(decoded.contains("## 复现步骤"), decoded);
        assertTrue(decoded.contains("## 环境信息"), decoded);
        assertTrue(decoded.contains("BPP-diagnostics-20261027-123456.zip"), decoded);
        assertTrue(!decoded.contains("/data/user/0/"), decoded);
        assertTrue(decoded.contains("请把下载文件夹里的 BPP-diagnostics-20261027-123456.zip 拖到这里上传"), decoded);
    }

    private static void testLongInputStillClamped() throws Exception {
        StringBuilder huge = new StringBuilder();
        for (int i = 0; i < 20000; i++) huge.append('中');
        String url = BppGithubFeedback.buildIssueUrl(
            "6.0.0-preview.44",
            "6.0.0-preview.44",
            "abc123",
            "Google",
            "Pixel 8",
            "15",
            35,
            "arm64-v8a",
            "/tmp/" + huge + ".zip"
        );
        assertTrue(url.startsWith(BppGithubFeedback.ISSUES_NEW_BASE), url);
        assertTrue(url.length() <= BppGithubFeedback.MAX_URL_CHARS, "clamped URL too long: " + url.length());
    }

    private static void assertTrue(boolean value, String message) {
        if (!value) throw new AssertionError(message);
    }
}
