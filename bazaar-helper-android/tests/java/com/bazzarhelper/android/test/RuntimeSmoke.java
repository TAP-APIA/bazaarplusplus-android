package com.bazzarhelper.android.test;

import android.app.*;
import android.content.*;
import android.os.*;
import android.view.*;
import android.widget.*;

/** Starts the installed game only; does not log in, enter a run, or play. */
public final class RuntimeSmoke extends Instrumentation {
  public void onCreate(Bundle b) {
    super.onCreate(b);
    start();
  }

  public void onStart() {
    try {
      Activity a =
          startActivitySync(
              new Intent()
                  .setClassName(
                      "com.bazzarhelper.android", "com.bazzarhelper.android.ui.MainActivity")
                  .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
      runOnMainSync(
          () -> {
            Button b = find(a.getWindow().getDecorView());
            if (b == null) throw new AssertionError("Launch button missing");
            b.performClick();
          });
      boolean ready = false;
      for (int i = 0; i < 240; i++) {
        if (com.bazzarhelper.android.HelperApplication.engine()
            .db
            .get("hookStatus", "")
            .contains("采集已就绪")) {
          ready = true;
          break;
        }
        Thread.sleep(1000);
      }
      Bundle out = new Bundle();
      out.putString(
          "stream",
          ready
              ? "Native hook READY; not a PvP test.\n"
              : "Native startup timed out; inspect logs.\n");
      finish(-1, out);
    } catch (Throwable e) {
      Bundle out = new Bundle();
      out.putString("stream", android.util.Log.getStackTraceString(e));
      finish(0, out);
    }
  }

  private Button find(View v) {
    if (v instanceof Button && "启动游戏".contentEquals(((Button) v).getText())) return (Button) v;
    if (v instanceof ViewGroup) {
      ViewGroup group = (ViewGroup) v;
      for (int i = 0; i < group.getChildCount(); i++) {
        Button b = find(group.getChildAt(i));
        if (b != null) return b;
      }
    }
    return null;
  }
}
