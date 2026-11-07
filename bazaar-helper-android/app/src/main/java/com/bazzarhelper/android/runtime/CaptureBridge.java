package com.bazzarhelper.android.runtime;

import com.bazzarhelper.android.HelperApplication;
import org.json.JSONObject;

/** The only managed hook -> app boundary; no HTTP is performed on a game callback. */
public final class CaptureBridge {
  public static void timer(int elapsedMilliseconds) {
    BattleTimerOverlay.update(elapsedMilliseconds);
  }

  public static String session() {
    try {
      return HelperApplication.engine().stamp();
    } catch (Exception e) {
      return "";
    }
  }

  public static boolean capture(String stamp, String data) {
    try {
      HelperApplication.engine().capture(stamp, new JSONObject(data));
      HelperApplication.wake();
      return true;
    } catch (Exception e) {
      android.util.Log.e("BazaarHelper", "Capture rejected: " + e.getClass().getSimpleName());
      return false;
    }
  }

  public static boolean begin(String data) {
    try {
      HelperApplication.engine().beginRun(new JSONObject(data));
      return true;
    } catch (Exception e) {
      return false;
    }
  }

  public static boolean complete(String run) {
    try {
      HelperApplication.engine().completeRun(run);
      HelperApplication.wake();
      return true;
    } catch (Exception e) {
      return false;
    }
  }

  public static void status(String value) {
    HelperApplication.engine().db.put("hookStatus", value);
    android.util.Log.i("BazaarHelper", value);
  }
}
