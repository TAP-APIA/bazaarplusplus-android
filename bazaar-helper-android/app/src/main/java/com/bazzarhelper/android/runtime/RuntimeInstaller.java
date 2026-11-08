package com.bazzarhelper.android.runtime;

import android.content.*;
import com.bazzarhelper.android.Diag;
import android.content.pm.*;
import java.io.*;
import java.nio.file.*;
import java.util.*;
import java.util.function.Consumer;
import java.util.regex.*;
import java.util.zip.*;

public final class RuntimeInstaller {
  public static final String GAME = "com.tempogames.thebazaar";
  private static final String RUNTIME_VERSION = "foundation-44e1e59-helper-1";

  /** What {@link #prepare} is about to do, so the UI can warn before a download starts. */
  public static final class Preview {
    public boolean unpackRuntime, gameChanged, downloadUnity, bundled;
    public String unityVersion = "";

    public boolean needed() {
      return unpackRuntime || gameChanged || downloadUnity;
    }

    public boolean downloads() {
      return downloadUnity;
    }
  }

  /**
   * Read-only check of the same stamps {@link #prepare} uses. Any read failure counts as "needs
   * work", which is the safe direction: the user gets the notice instead of a silent download.
   */
  public static Preview preview(Context c) {
    Preview p = new Preview();
    try {
      ApplicationInfo game = c.getPackageManager().getApplicationInfo(GAME, 0);
      File root = new File(c.getNoBackupFilesDir(), "runtime");
      File stamp = new File(root, "runtime.version");
      p.unpackRuntime = !stamp.isFile() || !Streams.text(stamp).equals(RUNTIME_VERSION);
      String fingerprint =
          game.sourceDir
              + ":"
              + new File(game.sourceDir).length()
              + ":"
              + new File(game.sourceDir).lastModified();
      File gameStamp = new File(root, "game.version");
      p.gameChanged = !gameStamp.isFile() || !Streams.text(gameStamp).equals(fingerprint);
      if (p.gameChanged) {
        p.downloadUnity = true;
        return p;
      }
      File data = new File(root, "game-data");
      p.unityVersion = unityVersion(data);
      String base = p.unityVersion.replaceAll("[a-z].*$", "");
      p.downloadUnity =
          !validUnityZip(new File(new File(root, "BepInEx/unity-libs"), base + ".zip"));
      if (p.downloadUnity) {
        p.bundled = bundledUnityLibs(c, base) != null;
        p.downloadUnity = !p.bundled;
      }
    } catch (Exception e) {
      p.unpackRuntime = true;
      p.downloadUnity = true;
    }
    return p;
  }

  public static RuntimeConfig prepare(Context c, Consumer<String> progress) throws Exception {
    ApplicationInfo game = c.getPackageManager().getApplicationInfo(GAME, 0);
    File root = new File(c.getNoBackupFilesDir(), "runtime"),
        bep = new File(root, "BepInEx"),
        dotnet = new File(root, "dotnet"),
        data = new File(root, "game-data"),
        code = new File(c.getDir("native-runtime", 0), "game");
    root.mkdirs();
    code.mkdirs();
    progress.accept("准备独立运行环境…");
    File stamp = new File(root, "runtime.version");
    if (!stamp.isFile() || !Streams.text(stamp).equals(RUNTIME_VERSION)) {
      unzip(c.getAssets().open("BepInEx.Android.zip"), bep, "");
      unzip(c.getAssets().open("dotnet.zip"), dotnet, "");
      Streams.text(stamp, RUNTIME_VERSION);
    }
    File plugin = new File(bep, "plugins/BazaarHelper.Hook.dll");
    plugin.getParentFile().mkdirs();
    copy(c.getAssets().open("BazaarHelper.Hook.dll"), plugin);
    String fingerprint =
        game.sourceDir
            + ":"
            + new File(game.sourceDir).length()
            + ":"
            + new File(game.sourceDir).lastModified();
    File gameStamp = new File(root, "game.version");
    if (!gameStamp.isFile() || !Streams.text(gameStamp).equals(fingerprint)) {
      progress.accept("提取当前游戏元数据…");
      delete(data);
      delete(new File(bep, "interop"));
      delete(new File(bep, "cache"));
      List<String> apks = new ArrayList<>();
      apks.add(game.sourceDir);
      if (game.splitSourceDirs != null) Collections.addAll(apks, game.splitSourceDirs);
      for (String apk : apks)
        try (ZipFile z = new ZipFile(apk)) {
          Enumeration<? extends ZipEntry> it = z.entries();
          while (it.hasMoreElements()) {
            ZipEntry e = it.nextElement();
            if (e.getName().startsWith("assets/bin/Data/") && !e.isDirectory()) {
              File target = safe(data, e.getName().substring("assets/bin/Data/".length()));
              copy(z.getInputStream(e), target);
            }
          }
        }
      Streams.text(gameStamp, fingerprint);
    }
    String unity = unityVersion(data);
    File libs = new File(bep, "unity-libs");
    libs.mkdirs();
    String base = unity.replaceAll("[a-z].*$", "");
    File unityZip = new File(libs, base + ".zip");
    if (!validUnityZip(unityZip)) {
      unityZip.delete();
      String bundled = bundledUnityLibs(c, base);
      if (bundled != null) {
        Diag.i("runtime", "unity libs from bundle: " + bundled + " for " + base);
        progress.accept("解出 Unity 代理依赖…");
        copy(c.getAssets().open("unity-libs/" + bundled + ".zip"), unityZip);
      } else {
        progress.accept("下载 Unity 代理依赖（几 MB，建议 Wi-Fi）…");
        String chosen = base;
        try {
          copy(download("https://unity.bepinex.dev/libraries/" + chosen + ".zip"), unityZip);
        } catch (IOException exact) {
          unityZip.delete();
          String listing;
          java.net.URLConnection conn =
              new java.net.URL("https://unity.bepinex.dev/libraries/").openConnection();
          conn.setConnectTimeout(15000);
          conn.setReadTimeout(20000);
          try (InputStream in = conn.getInputStream()) {
            listing =
                new String(
                    Streams.read(in, 4 * 1024 * 1024), java.nio.charset.StandardCharsets.UTF_8);
          }
          String prefix = base.substring(0, base.lastIndexOf('.') + 1);
          int patch = Integer.parseInt(base.substring(base.lastIndexOf('.') + 1)), best = -1;
          Matcher m = Pattern.compile(Pattern.quote(prefix) + "(\\d+)\\.zip").matcher(listing);
          while (m.find()) {
            int n = Integer.parseInt(m.group(1));
            if (n <= patch) best = Math.max(best, n);
          }
          if (best < 0) throw exact;
          chosen = prefix + best;
          copy(download("https://unity.bepinex.dev/libraries/" + chosen + ".zip"), unityZip);
        }
      }
    }
    if (!validUnityZip(unityZip)) {
      unityZip.delete();
      throw new IOException("Unity 依赖下载不完整，请重试");
    }
    File config = new File(bep, "config/BepInEx.cfg");
    String cfg = Streams.text(config);
    cfg =
        cfg.replaceAll(
            "(?m)^UnityBaseLibrariesSource\\s*=.*$",
            "UnityBaseLibrariesSource = https://unity.bepinex.dev/libraries/{VERSION}.zip");
    Streams.text(config, cfg);
    RuntimeConfig r = new RuntimeConfig();
    r.gameLibraryDirectory = game.nativeLibraryDir;
    r.appLibraryDirectory = c.getApplicationInfo().nativeLibraryDir;
    r.appDataDirectory = new File(root, "app-data").getAbsolutePath();
    new File(r.appDataDirectory).mkdirs();
    r.codeCacheDirectory = code.getAbsolutePath();
    r.bepInExDirectory = bep.getAbsolutePath();
    r.dotnetDirectory = dotnet.getAbsolutePath();
    r.unityDataDirectory = data.getAbsolutePath();
    r.unityVersion = unity;
    // The original game remains installed separately; runtime owns only helper-private files.
    progress.accept("环境就绪，正在生成游戏代理（首次较慢，请保持前台）…");
    return r;
  }

  private static String unityVersion(File root) throws IOException {
    for (String name : new String[] {"globalgamemanagers", "data.unity3d", "mainData"}) {
      File f = new File(root, name);
      if (!f.isFile()) continue;
      try (InputStream in = new FileInputStream(f)) {
        byte[] b = new byte[512];
        int length = in.read(b);
        if (length < 0) continue;
        Matcher m =
            Pattern.compile("\\d{4}\\.\\d+\\.\\d+[abfp]\\d+")
                .matcher(new String(b, 0, length, java.nio.charset.StandardCharsets.ISO_8859_1));
        if (m.find()) return m.group();
      }
    }
    throw new IOException("无法识别 Unity 版本");
  }

  /**
   * Bundled Unity base libraries are stored under the game's Unity version, so this is an exact
   * filename match and nothing else: a patch the game does not run is never substituted (that
   * fallback stays in the download path, which mirrors BepInEx). Anything else returns null and
   * prepare() downloads as before.
   */
  private static String bundledUnityLibs(Context c, String base) {
    try {
      for (String name : c.getAssets().list("unity-libs")) if (name.equals(base + ".zip")) return base;
    } catch (IOException ignored) {
    }
    return null;
  }

  private static InputStream download(String url) throws IOException {
    java.net.URLConnection c = new java.net.URL(url).openConnection();
    c.setConnectTimeout(15000);
    c.setReadTimeout(60000);
    return c.getInputStream();
  }

  private static boolean validUnityZip(File f) {
    if (!f.isFile()) return false;
    try (ZipFile z = new ZipFile(f)) {
      Enumeration<? extends ZipEntry> e = z.entries();
      while (e.hasMoreElements()) if (e.nextElement().getName().endsWith(".dll")) return true;
    } catch (IOException ignored) {
    }
    return false;
  }

  private static File safe(File root, String name) throws IOException {
    File f = new File(root, name);
    if (!f.getCanonicalPath().startsWith(root.getCanonicalPath() + File.separator))
      throw new IOException("无效归档路径");
    return f;
  }

  private static void unzip(InputStream input, File root, String prefix) throws IOException {
    root.mkdirs();
    try (ZipInputStream z = new ZipInputStream(input)) {
      ZipEntry e;
      while ((e = z.getNextEntry()) != null) {
        if (e.isDirectory()) continue;
        File f = safe(root, e.getName());
        f.getParentFile().mkdirs();
        try (FileOutputStream out = new FileOutputStream(f)) {
          Streams.transfer(z, out);
        }
      }
    }
  }

  private static void copy(InputStream in, File target) throws IOException {
    target.getParentFile().mkdirs();
    File tmp = new File(target.getPath() + ".tmp");
    try (InputStream source = in;
        FileOutputStream out = new FileOutputStream(tmp)) {
      Streams.transfer(source, out);
      out.getFD().sync();
    }
    Files.move(tmp.toPath(), target.toPath(), StandardCopyOption.REPLACE_EXISTING);
  }

  private static void delete(File f) throws IOException {
    if (!f.exists()) return;
    if (f.isDirectory()) {
      File[] files = f.listFiles();
      if (files != null) for (File child : files) delete(child);
    }
    Files.delete(f.toPath());
  }
}
