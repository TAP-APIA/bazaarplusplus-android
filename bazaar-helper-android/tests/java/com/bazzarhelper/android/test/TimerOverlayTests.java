package com.bazzarhelper.android.test;

import android.app.*;
import android.content.*;
import android.content.pm.ActivityInfo;
import android.graphics.*;
import android.os.*;
import android.view.*;
import android.widget.*;
import com.bazzarhelper.android.runtime.BattleTimerOverlay;
import java.io.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;

/**
 * Native layout/touch verification using a display-only sample, never a game or recorder message.
 */
public final class TimerOverlayTests extends Instrumentation {
  private final AtomicReference<Activity> current = new AtomicReference<>();
  private final ConcurrentHashMap<String, String> settings = new ConcurrentHashMap<>();
  private final BattleTimerOverlay.Store store =
      new BattleTimerOverlay.Store() {
        public String get(String key, String fallback) {
          String value = settings.get(key);
          return value == null ? fallback : value;
        }

        public void put(String key, String value) {
          settings.put(key, value);
        }
      };
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
    try {
      ((Application) getTargetContext().getApplicationContext())
          .registerActivityLifecycleCallbacks(
              new Application.ActivityLifecycleCallbacks() {
                public void onActivityResumed(Activity a) {
                  current.set(a);
                }

                public void onActivityCreated(Activity a, Bundle b) {}

                public void onActivityStarted(Activity a) {}

                public void onActivityPaused(Activity a) {}

                public void onActivityStopped(Activity a) {}

                public void onActivitySaveInstanceState(Activity a, Bundle b) {}

                public void onActivityDestroyed(Activity a) {}
              });
      Activity initial =
          startActivitySync(
              new Intent()
                  .setClassName(
                      "com.bazzarhelper.android", "com.bazzarhelper.android.ui.MainActivity")
                  .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
      runOnMainSync(
          () -> initial.setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE));
      for (int i = 0; i < 100; i++) {
        Activity a = current.get();
        if (a != null && a.getResources().getConfiguration().orientation == 2) break;
        Thread.sleep(50);
      }
      waitForIdleSync();
      Activity a = current.get();
      check(
          a != null && a.getResources().getConfiguration().orientation == 2,
          "unlock the device first: rotation is blocked while the screen is locked");
      AtomicInteger touches = new AtomicInteger();
      runOnMainSync(
          () -> {
            View probe = new View(a);
            probe.setOnClickListener(v -> touches.incrementAndGet());
            ((ViewGroup) a.getWindow().getDecorView())
                .addView(probe, new ViewGroup.LayoutParams(-1, -1));
            BattleTimerOverlay.useStore(store);
            BattleTimerOverlay.attach(a);
            BattleTimerOverlay.update(61950);
          });
      Thread.sleep(150);
      waitForIdleSync();
      final int[] location = new int[2];
      final TextView[] found = new TextView[1];
      runOnMainSync(
          () -> {
            TextView t = find(a.getWindow().getDecorView());
            found[0] = t;
            check(t != null && t.getVisibility() == View.VISIBLE, "visible");
            check("1:01.95".contentEquals(t.getText()), "logical time format");
            View root = a.getWindow().getDecorView();
            t.getLocationOnScreen(location);
            int[] origin = new int[2];
            root.getLocationOnScreen(origin);
            check(root.getWidth() > root.getHeight(), "landscape preview");
            check(
                Math.abs(location[0] + t.getWidth() / 2.0 - origin[0] - root.getWidth() / 2.0) <= 1,
                "horizontal center");
            float density = t.getResources().getDisplayMetrics().density;
            int gap = origin[1] + root.getHeight() - location[1] - t.getHeight();
            check(gap >= Math.round(24 * density), "bottom padding including safe area");
            check(!t.isFocusable(), "no focus stealing");
            check(
                t.getPaddingLeft() == Math.round(14 * density)
                    && t.getPaddingTop() == Math.round(6 * density),
                "inner padding");
          });
      Bitmap screenshot = getUiAutomation().takeScreenshot();
      File output =
          new File(getTargetContext().getExternalFilesDir(null), "timer-overlay-preview.png");
      try (OutputStream out = new FileOutputStream(output)) {
        screenshot.compress(Bitmap.CompressFormat.PNG, 100, out);
      }
      screenshot.recycle();
      TextView t = found[0];

      // Only the pill takes touches; the rest of the screen stays with the game.
      final int[] origin = new int[2];
      final int[] window = new int[2];
      runOnMainSync(
          () -> {
            View root = a.getWindow().getDecorView();
            root.getLocationOnScreen(origin);
            window[0] = root.getWidth();
            window[1] = root.getHeight();
          });
      tap(origin[0] + window[0] / 5f, origin[1] + window[1] / 3f);
      waitForIdleSync();
      check(touches.get() == 1, "touches outside the timer pass through");

      // Dragging moves the pill and persists a resolution-independent position.
      // Warm-up drag: a late relayout (text width, insets) re-centres the pill once.
      drag(t, -80, -60);
      Thread.sleep(250);
      float[] before = drag(t, -260, -180);
      float beforeX = before[0], beforeY = before[1];
      float afterX = translation(t, 0), afterY = translation(t, 1);
      // Screen-level injection can be offset by the window/cutout transform on some devices, so
      // assert the direction and the requested distance within a tenth rather than exact pixels.
      check(follows(afterX - beforeX, -260), "drag follows horizontal delta");
      check(follows(afterY - beforeY, -180), "drag follows vertical delta");
      check(
          Math.abs(afterX - beforeX) > 100 && Math.abs(afterY - beforeY) > 100, "timer moved");
      check(touches.get() == 1, "drag does not reach the game");
      check("1:01.95".contentEquals(t.getText()), "drag keeps the time");
      final int[] free = new int[2];
      runOnMainSync(
          () -> {
            View root = a.getWindow().getDecorView();
            free[0] = Math.max(1, root.getWidth() - t.getWidth());
            free[1] = Math.max(1, root.getHeight() - t.getHeight());
          });
      String saved = await(BattleTimerOverlay.KEY_POSITION);
      check(saved != null && saved.contains(","), "position persisted");
      String[] parts = saved.split(",");
      check(
          Math.abs(Float.parseFloat(parts[0]) - afterX / free[0]) <= 0.02,
          "persisted horizontal fraction");
      check(
          Math.abs(Float.parseFloat(parts[1]) - afterY / free[1]) <= 0.02,
          "persisted vertical fraction");

      // A drag past the edge clamps the pill inside the window.
      final int[] screen = new int[2];
      runOnMainSync(
          () -> {
            View root = a.getWindow().getDecorView();
            screen[0] = origin[0] + root.getWidth() - 2;
            screen[1] = origin[1] + root.getHeight() - 2;
          });
      dragTo(t, screen[0], screen[1]);
      int[] clamped = new int[2];
      runOnMainSync(
          () -> {
            t.getLocationOnScreen(clamped);
            check(
                clamped[0] + t.getWidth() >= origin[0] + free[0] + t.getWidth() - 2,
                "clamped to right edge");
            check(
                clamped[1] + t.getHeight() >= origin[1] + free[1] + t.getHeight() - 2,
                "clamped to bottom edge");
          });

      // The helper UI toggle hides and shows the pill.
      settings.put(BattleTimerOverlay.KEY_VISIBLE, "0");
      runOnMainSync(() -> BattleTimerOverlay.reload());
      check(awaitGone(found[0]), "hidden by setting");
      settings.put(BattleTimerOverlay.KEY_VISIBLE, "1");
      runOnMainSync(() -> BattleTimerOverlay.reload());
      check(awaitVisible(found[0]), "shown by setting");

      // Resetting the position returns the pill to the default bottom-center spot.
      settings.put(BattleTimerOverlay.KEY_POSITION, "");
      runOnMainSync(() -> BattleTimerOverlay.reload());
      Thread.sleep(200);
      waitForIdleSync();
      runOnMainSync(
          () -> {
            t.getLocationOnScreen(location);
            float density = t.getResources().getDisplayMetrics().density;
            check(
                Math.abs(location[0] + t.getWidth() / 2.0 - origin[0] - free[0] / 2.0 - t.getWidth() / 2.0)
                    <= 2,
                "reset returns to horizontal center");
            int gap = origin[1] + free[1] + t.getHeight() - location[1] - t.getHeight();
            check(gap >= Math.round(24 * density) - 2, "reset returns to bottom padding");
          });

      // The switch persists through the same key/value seam the helper UI uses.
      runOnMainSync(
          () -> {
            BattleTimerOverlay.setEnabled(store, false);
            check(!BattleTimerOverlay.isEnabled(store), "disable is stored");
            BattleTimerOverlay.setEnabled(store, true);
            check(BattleTimerOverlay.isEnabled(store), "enable is stored");
          });

      runOnMainSync(() -> BattleTimerOverlay.update(-1));
      Thread.sleep(100);
      waitForIdleSync();
      runOnMainSync(() -> check(found[0].getVisibility() == View.GONE, "hide on exit"));
      check("0:00.00".equals(BattleTimerOverlay.format(0)), "zero format");
      check("1:00.00".equals(BattleTimerOverlay.format(60000)), "minute rollover");
      check("0:00.05".equals(BattleTimerOverlay.format(50)), "centisecond precision");
      Bundle result = new Bundle();
      result.putString(
          "stream",
          "PASS " + checks + " timer overlay checks; preview: " + output.getAbsolutePath() + "\n");
      finish(-1, result);
    } catch (Throwable e) {
      Bundle result = new Bundle();
      result.putString("stream", android.util.Log.getStackTraceString(e));
      finish(0, result);
    }
  }

  private float translation(TextView t, int axis) {
    final float[] value = new float[1];
    runOnMainSync(
        () -> value[0] = axis == 0 ? t.getTranslationX() : t.getTranslationY());
    return value[0];
  }

  /**
   * Screen-level injection: targeting our own window is refused whenever a system window (gesture
   * bar, IME, heads-up) owns the point, which made these checks flaky.
   */
  private void inject(MotionEvent event) {
    getUiAutomation().injectInputEvent(event, true);
    try {
      Thread.sleep(16);
    } catch (InterruptedException ignored) {
    }
  }

  private void tap(float x, float y) {
    long now = SystemClock.uptimeMillis();
    MotionEvent down = MotionEvent.obtain(now, now, MotionEvent.ACTION_DOWN, x, y, 0);
    MotionEvent up = MotionEvent.obtain(now, now + 40, MotionEvent.ACTION_UP, x, y, 0);
    inject(down);
    inject(up);
    down.recycle();
    up.recycle();
  }

  private static boolean follows(float moved, float requested) {
    return Math.signum(moved) == Math.signum(requested)
        && Math.abs(Math.abs(moved) - Math.abs(requested)) <= Math.max(20, Math.abs(requested) * 0.12);
  }

  /** Drags by the given screen delta and returns the translation the pill started from. */
  private float[] drag(TextView t, float dx, float dy) throws Exception {
    final int[] loc = new int[2];
    final float[] start = new float[2];
    runOnMainSync(
        () -> {
          t.getLocationOnScreen(loc);
          start[0] = t.getTranslationX();
          start[1] = t.getTranslationY();
        });
    dragTo(t, loc[0] + t.getWidth() / 2f + dx, loc[1] + t.getHeight() / 2f + dy);
    return start;
  }

  private void dragTo(TextView t, float targetX, float targetY) throws Exception {
    final int[] loc = new int[2];
    runOnMainSync(() -> t.getLocationOnScreen(loc));
    float x = loc[0] + t.getWidth() / 2f, y = loc[1] + t.getHeight() / 2f;
    float dx = targetX - x, dy = targetY - y;
    long now = SystemClock.uptimeMillis();
    MotionEvent down = MotionEvent.obtain(now, now, MotionEvent.ACTION_DOWN, x, y, 0);
    inject(down);
    down.recycle();
    for (int i = 1; i <= 6; i++) {
      MotionEvent move =
          MotionEvent.obtain(
              now, now + i * 16, MotionEvent.ACTION_MOVE, x + dx * i / 6f, y + dy * i / 6f, 0);
      inject(move);
      move.recycle();
    }
    MotionEvent up =
        MotionEvent.obtain(now, now + 150, MotionEvent.ACTION_UP, x + dx, y + dy, 0);
    inject(up);
    up.recycle();
    Thread.sleep(80);
    waitForIdleSync();
  }

  private String await(String key) throws Exception {
    for (int i = 0; i < 60; i++) {
      String value = settings.get(key);
      if (value != null && !value.isEmpty()) return value;
      Thread.sleep(50);
    }
    return null;
  }

  private boolean awaitGone(TextView t) throws Exception {
    for (int i = 0; i < 60; i++) {
      final boolean[] gone = new boolean[1];
      runOnMainSync(() -> gone[0] = t.getVisibility() == View.GONE);
      if (gone[0]) return true;
      Thread.sleep(50);
    }
    return false;
  }

  private boolean awaitVisible(TextView t) throws Exception {
    for (int i = 0; i < 60; i++) {
      final boolean[] shown = new boolean[1];
      runOnMainSync(() -> shown[0] = t.getVisibility() == View.VISIBLE);
      if (shown[0]) return true;
      Thread.sleep(50);
    }
    return false;
  }

  private TextView find(View v) {
    if (v instanceof TextView && android.text.TextUtils.equals("战斗用时", v.getContentDescription()))
      return (TextView) v;
    if (v instanceof ViewGroup) {
      ViewGroup g = (ViewGroup) v;
      for (int i = 0; i < g.getChildCount(); i++) {
        TextView r = find(g.getChildAt(i));
        if (r != null) return r;
      }
    }
    return null;
  }
}
