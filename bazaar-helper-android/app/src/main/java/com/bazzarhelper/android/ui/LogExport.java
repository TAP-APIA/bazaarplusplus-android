package com.bazzarhelper.android.ui;

import android.app.Activity;
import android.app.ActivityManager;
import android.content.ContentValues;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.provider.MediaStore;
import android.util.DisplayMetrics;
import com.bazzarhelper.android.*;
import com.bazzarhelper.android.runtime.BattleTimerOverlay;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.*;
import java.util.zip.Deflater;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

/**
 * Support bundle: device/app snapshot plus the rolling diagnostic logs, zipped into Downloads so it
 * can be shared without a FileProvider. Tokens and device secrets are never included.
 */
final class LogExport {
  private LogExport() {}

  static final class Result {
    final String name;
    final Uri uri;
    final int bytes;

    Result(String name, Uri uri, int bytes) {
      this.name = name;
      this.uri = uri;
      this.bytes = bytes;
    }
  }

  static Result write(Activity activity) throws Exception {
    String stamp = new SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).format(new Date());
    String name = "bazaarhelper-diag-" + stamp + ".zip";
    File dir = activity.getFilesDir();
    byte[] archive = archive(report(activity, logs(dir)), dir);

    ContentValues values = new ContentValues();
    values.put(MediaStore.MediaColumns.DISPLAY_NAME, name);
    values.put(MediaStore.MediaColumns.MIME_TYPE, "application/zip");
    values.put(
        MediaStore.MediaColumns.RELATIVE_PATH,
        Environment.DIRECTORY_DOWNLOADS + "/BazaarHelper");
    values.put(MediaStore.MediaColumns.IS_PENDING, 1);
    Uri uri =
        activity
            .getContentResolver()
            .insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values);
    if (uri == null) throw new IllegalStateException("无法创建导出文件");
    try (OutputStream out = activity.getContentResolver().openOutputStream(uri)) {
      if (out == null) throw new IllegalStateException("无法写入导出文件");
      out.write(archive);
    }
    values.clear();
    values.put(MediaStore.MediaColumns.IS_PENDING, 0);
    activity.getContentResolver().update(uri, values, null, null);
    Diag.i("export", name + " (" + archive.length + " bytes)");
    return new Result(name, uri, archive.length);
  }

  /** report.txt plus every log file, deflated; text shrinks to a fraction of its size. */
  static byte[] archive(String report, File logDir) throws Exception {
    ByteArrayOutputStream bytes = new ByteArrayOutputStream(32 * 1024);
    try (ZipOutputStream zip = new ZipOutputStream(bytes)) {
      zip.setLevel(Deflater.BEST_COMPRESSION);
      entry(zip, "report.txt", report);
      for (String log : new String[] {"app.log", "app.log.1", "game.log", "game.log.1"}) {
        File source = new File(logDir, log);
        if (source.isFile()) entry(zip, "logs/" + log, Diag.tail(source, 512 * 1024));
      }
    }
    return bytes.toByteArray();
  }

  private static void entry(ZipOutputStream zip, String name, String text) throws Exception {
    zip.putNextEntry(new ZipEntry(name));
    zip.write(text.getBytes(StandardCharsets.UTF_8));
    zip.closeEntry();
  }

  /** Log files that actually exist, so the report never advertises empty entries. */
  private static List<String> logs(File dir) {
    List<String> entries = new ArrayList<>();
    for (String log : new String[] {"app.log", "app.log.1", "game.log", "game.log.1"})
      if (new File(dir, log).isFile()) entries.add("logs/" + log);
    return entries;
  }

  private static String report(Activity activity, List<String> entries) {
    StringBuilder text = new StringBuilder(8192);
    text.append("大巴扎助手 / BazaarHelper 诊断导出\n");
    text.append("生成时间: ")
        .append(new SimpleDateFormat("yyyy-MM-dd HH:mm:ss Z", Locale.US).format(new Date()))
        .append('\n');
    text.append("应用: ")
        .append(activity.getPackageName())
        .append(' ')
        .append(version(activity))
        .append('\n');
    DisplayMetrics metrics = activity.getResources().getDisplayMetrics();
    text.append("设备: ")
        .append(Build.MANUFACTURER)
        .append(' ')
        .append(Build.MODEL)
        .append(" · Android ")
        .append(Build.VERSION.RELEASE)
        .append(" (SDK ")
        .append(Build.VERSION.SDK_INT)
        .append(") · ")
        .append(metrics.widthPixels)
        .append('x')
        .append(metrics.heightPixels)
        .append(" @")
        .append(metrics.density)
        .append("x · ")
        .append(Build.SUPPORTED_ABIS.length > 0 ? Build.SUPPORTED_ABIS[0] : "?")
        .append('\n');
    text.append(gameRunning(activity) ? "游戏进程: 运行中\n" : "游戏进程: 未运行\n");

    try {
      org.json.JSONObject session = HelperApplication.engine().db.session();
      boolean bound = !session.optString("owner").isEmpty();
      text.append("账号: ")
          .append(bound ? "已绑定 " + session.optString("alias", "微信账号") : "未绑定")
          .append(" · 凭据 ")
          .append(session.optString("token").isEmpty() ? "无" : "存在")
          .append(" · 代次 ")
          .append(shortId(session.optString("generation")))
          .append('\n');
      if (bound)
        text.append("归属: ")
            .append(shortId(session.optString("owner")))
            .append(" · 本地战绩 ")
            .append(
                HelperApplication.engine()
                    .db
                    .records(session.optString("owner"), null)
                    .size())
            .append(" 场\n");
      text.append("同步: ")
          .append(HelperApplication.engine().db.get("syncStatus", "尚未同步"))
          .append('\n');
      text.append("采集: ")
          .append(HelperApplication.engine().db.get("hookStatus", "未初始化"))
          .append('\n');
      text.append("计时器: ")
          .append(
              BattleTimerOverlay.isEnabled(BattleTimerOverlay.defaultStore()) ? "显示" : "隐藏")
          .append(" · 位置 ")
          .append(
              value(
                  BattleTimerOverlay.defaultStore().get(BattleTimerOverlay.KEY_POSITION, ""),
                  "默认"))
          .append('\n');
    } catch (Exception e) {
      text.append("账号: 读取失败 (").append(e.getClass().getSimpleName()).append(")\n");
    }

    text.append("\n压缩包内容: report.txt（本文件）")
        .append(entries.isEmpty() ? "，暂无日志文件" : "、" + String.join("、", entries))
        .append('\n');
    return text.toString();
  }

  private static String version(Activity activity) {
    try {
      android.content.pm.PackageInfo info =
          activity
              .getPackageManager()
              .getPackageInfo(activity.getPackageName(), 0);
      return info.versionName
          + " ("
          + (Build.VERSION.SDK_INT >= 28 ? info.getLongVersionCode() : info.versionCode)
          + ")";
    } catch (Exception e) {
      return "0.1.2";
    }
  }

  private static boolean gameRunning(Activity activity) {
    ActivityManager manager = activity.getSystemService(ActivityManager.class);
    List<ActivityManager.RunningAppProcessInfo> processes = manager.getRunningAppProcesses();
    if (processes == null) return false;
    for (ActivityManager.RunningAppProcessInfo process : processes)
      if ((activity.getPackageName() + ":game").equals(process.processName)) return true;
    return false;
  }

  private static String shortId(String value) {
    return value == null || value.isEmpty() ? "无" : value.substring(0, Math.min(8, value.length()));
  }

  private static String value(String raw, String fallback) {
    return raw == null || raw.isEmpty() ? fallback : raw;
  }
}
