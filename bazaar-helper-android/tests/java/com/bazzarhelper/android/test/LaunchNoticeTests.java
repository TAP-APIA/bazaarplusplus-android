package com.bazzarhelper.android.test;

import android.app.*;
import android.content.Intent;
import android.os.Bundle;
import android.view.View;
import android.view.ViewGroup;
import android.widget.TextView;
import java.io.*;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.List;

/**
 * The launch notice must appear whenever the helper has to prepare (and maybe download) the runtime.
 * This looks at the real screen through the accessibility tree, so it also proves the wording is
 * reachable, and it restores the runner stamps before finishing so nothing is re-downloaded.
 */
public final class LaunchNoticeTests extends Instrumentation {
  private int checks;

  private void check(boolean value, String name) {
    checks++;
    if (!value) throw new AssertionError(name);
  }

  public void onCreate(Bundle b) {
    super.onCreate(b);
    start();
  }

  public void onStart() {
    File stamp = null;
    String saved = null;
    try {
      Activity a =
          startActivitySync(
              new Intent()
                  .setClassName(
                      "com.bazzarhelper.android", "com.bazzarhelper.android.ui.MainActivity")
                  .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
      waitForIdleSync();
      Thread.sleep(500);

      stamp = new File(new File(a.getNoBackupFilesDir(), "runtime"), "game.version");
      saved = stamp.isFile() ? read(stamp) : null;
      // Pretend the installed game changed: prepare() would re-extract and download the Unity zip.
      if (stamp.isFile()) check(stamp.delete(), "runner stamp removed");

      Method launch = a.getClass().getDeclaredMethod("launch");
      launch.setAccessible(true);
      final Method call = launch;
      a.runOnUiThread(
          () -> {
            try {
              call.invoke(a);
            } catch (Exception e) {
              throw new IllegalStateException(e);
            }
          });
      waitForIdleSync();
      Thread.sleep(900);

      // The keyguard may own the "active window", so look at every root view instead.
      check(find("先准备运行环境"), "notice title");
      check(find("Unity 代理依赖"), "notice mentions the download");
      check(find("Wi-Fi"), "notice suggests Wi-Fi");
      check(find("开始准备"), "notice has a start action");
      check(click("取消"), "notice has a cancel action");
      waitForIdleSync();
      Thread.sleep(500);
      check(!find("先准备运行环境"), "notice dismissed");

      // Put the stamp back and prove the next launch will not redo the download.
      if (saved != null) write(stamp, saved);
      stamp = null;
      com.bazzarhelper.android.runtime.RuntimeInstaller.Preview preview =
          com.bazzarhelper.android.runtime.RuntimeInstaller.preview(a);
      check(!preview.gameChanged, "runner stamp restored");
      check(!preview.needed(), "prepared runtime stays prepared");

      Bundle out = new Bundle();
      out.putString("stream", "PASS " + checks + " launch notice checks\n");
      finish(-1, out);
    } catch (Throwable e) {
      Bundle out = new Bundle();
      out.putString("stream", android.util.Log.getStackTraceString(e));
      finish(0, out);
    } finally {
      if (stamp != null && saved != null)
        try {
          write(stamp, saved);
        } catch (IOException ignored) {
        }
    }
  }

  private static String read(File file) throws IOException {
    try (InputStream in = new FileInputStream(file)) {
      ByteArrayOutputStream out = new ByteArrayOutputStream();
      byte[] buffer = new byte[4096];
      int n;
      while ((n = in.read(buffer)) > 0) out.write(buffer, 0, n);
      return out.toString("UTF-8");
    }
  }

  private static void write(File file, String value) throws IOException {
    file.getParentFile().mkdirs();
    try (OutputStream out = new FileOutputStream(file)) {
      out.write(value.getBytes("UTF-8"));
    }
  }

  private boolean find(String text) {
    return view(text) != null;
  }

  private boolean click(String text) throws Exception {
    View target = view(text);
    if (target == null) return false;
    final boolean[] clicked = new boolean[1];
    runOnMainSync(() -> clicked[0] = target.performClick());
    return clicked[0];
  }

  /** Searches every window root, including dialogs that the keyguard hides from accessibility. */
  private View view(String text) {
    try {
      Class<?> global = Class.forName("android.view.WindowManagerGlobal");
      Object instance = global.getMethod("getInstance").invoke(null);
      Field views = global.getDeclaredField("mViews");
      views.setAccessible(true);
      for (Object entry : (List<?>) views.get(instance)) {
        View found = view((View) entry, text);
        if (found != null) return found;
      }
    } catch (Exception ignored) {
    }
    return null;
  }

  private static View view(View root, String text) {
    if (root == null) return null;
    // ChunkyButton draws its label, so the label only exists as the content description.
    CharSequence description = root.getContentDescription();
    if (description != null && description.toString().contains(text) && root.isShown())
      return root;
    if (root instanceof TextView) {
      CharSequence value = ((TextView) root).getText();
      if (value != null && value.toString().contains(text) && root.isShown()) return root;
    }
    if (root instanceof ViewGroup) {
      ViewGroup group = (ViewGroup) root;
      for (int i = 0; i < group.getChildCount(); i++) {
        View found = view(group.getChildAt(i), text);
        if (found != null) return found;
      }
    }
    return null;
  }
}
