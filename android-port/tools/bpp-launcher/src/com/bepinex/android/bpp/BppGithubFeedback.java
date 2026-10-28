package com.bepinex.android.bpp;

import java.io.UnsupportedEncodingException;
import java.net.URLEncoder;

/** Pure URL/template helper for the manual GitHub feedback flow. No network I/O. */
public final class BppGithubFeedback {

    public static final String ISSUES_NEW_BASE =
        "https://github.com/TAP-APIA/bazaarplusplus-android/issues/new";
    public static final int MAX_URL_CHARS = 6000;
    public static final String DEFAULT_ZIP_FILE_NAME = "（未生成）";

    private BppGithubFeedback() {}

    public static String buildIssueUrl(
        String launcherVersion,
        String bppVersionName,
        String bppCommit,
        String manufacturer,
        String model,
        String androidRelease,
        int sdkInt,
        String abis,
        String zipFileName
    ) {
        String safeZip = fileNameOnly(zipFileName);
        if (safeZip.length() == 0) safeZip = DEFAULT_ZIP_FILE_NAME;
        String title = buildTitle();
        String body = buildBody(
            launcherVersion,
            bppVersionName,
            bppCommit,
            manufacturer,
            model,
            androidRelease,
            sdkInt,
            abis,
            safeZip
        );
        String encodedTitle = encodeQuery(title);
        String encodedBody = encodeQuery(body);
        String url = composeUrl(encodedTitle, encodedBody);
        if (url.length() <= MAX_URL_CHARS) return clampUrl(url);

        int low = 0;
        int high = body.length();
        String best = composeUrl(encodedTitle, encodeQuery(""));
        if (best.length() > MAX_URL_CHARS) return clampUrl(best);
        while (low <= high) {
            int mid = low + (high - low) / 2;
            String part = body.substring(0, Math.min(mid, body.length()));
            String candidate = composeUrl(encodedTitle, encodeQuery(part));
            if (candidate.length() <= MAX_URL_CHARS) {
                best = candidate;
                low = mid + 1;
            } else {
                high = mid - 1;
            }
        }
        return clampUrl(best);
    }

    public static String buildTitle() {
        return "[Android] 启动器问题反馈";
    }

    public static String buildBody(
        String launcherVersion,
        String bppVersionName,
        String bppCommit,
        String manufacturer,
        String model,
        String androidRelease,
        int sdkInt,
        String abis,
        String zipFileName
    ) {
        String safeZip = fileNameOnly(zipFileName);
        if (safeZip.length() == 0) safeZip = DEFAULT_ZIP_FILE_NAME;
        StringBuilder sb = new StringBuilder();
        sb.append("## 问题描述\n\n\n");
        sb.append("## 复现步骤\n\n1. \n2. \n3. \n\n");
        sb.append("## 环境信息\n");
        sb.append("- 启动器版本: ").append(safeValue(launcherVersion, "未知")).append('\n');
        sb.append("- BppBuildInfo: VERSION_NAME=").append(safeValue(bppVersionName, "未知"))
            .append(" COMMIT=").append(safeValue(bppCommit, "未知")).append('\n');
        sb.append("- 设备: ").append(safeValue(manufacturer, "未知"))
            .append(' ').append(safeValue(model, "未知")).append('\n');
        sb.append("- Android: ").append(safeValue(androidRelease, "未知"))
            .append(" (SDK ").append(sdkInt).append(")\n");
        sb.append("- ABI: ").append(safeValue(abis, "未知")).append('\n');
        sb.append("- 诊断包文件名: ").append(safeZip).append("\n\n");
        sb.append("请把下载文件夹里的 ").append(safeZip).append(" 拖到这里上传\n");
        return sb.toString();
    }

    public static String fileNameOnly(String pathOrName) {
        if (pathOrName == null) return "";
        String value = pathOrName.trim();
        if (value.length() == 0) return "";
        value = value.replace('\\', '/');
        int slash = value.lastIndexOf('/');
        String name = slash >= 0 ? value.substring(slash + 1) : value;
        if (name.length() == 0) return "";
        if (".".equals(name) || "..".equals(name)) return "";
        return name;
    }

    public static String encodeQuery(String value) {
        String safe = value == null ? "" : value;
        try {
            return URLEncoder.encode(safe, "UTF-8").replace("+", "%20");
        } catch (UnsupportedEncodingException ex) {
            return safe;
        }
    }

    public static String clampUrl(String url) {
        if (url == null) return "";
        if (url.length() <= MAX_URL_CHARS) return url;
        String clamped = url.substring(0, MAX_URL_CHARS);
        int percent = clamped.lastIndexOf('%');
        if (percent >= 0 && percent > clamped.length() - 3) {
            clamped = clamped.substring(0, percent);
        }
        return clamped;
    }

    private static String composeUrl(String encodedTitle, String encodedBody) {
        return ISSUES_NEW_BASE + "?title=" + encodedTitle + "&body=" + encodedBody;
    }

    private static String safeValue(String value, String fallback) {
        if (value == null) return fallback;
        String trimmed = value.trim();
        if (trimmed.length() == 0) return fallback;
        return trimmed.replace('\r', ' ').replace('\n', ' ');
    }
}
