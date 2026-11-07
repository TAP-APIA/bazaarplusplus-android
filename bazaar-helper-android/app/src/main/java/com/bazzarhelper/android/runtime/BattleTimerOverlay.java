package com.bazzarhelper.android.runtime;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.*;
import android.view.*;
import android.widget.*;
import com.bazzarhelper.android.HelperApplication;
import java.lang.ref.WeakReference;
import java.util.Locale;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;

/**
 * A draggable timer pill on the game window; no overlay permission or game UI injection. Only the
 * pill itself takes touches (to drag it); everything else goes to the game.
 */
public final class BattleTimerOverlay {
  public static final int BOTTOM_PADDING_DP = 24;

  /** Shared between the helper UI process and the :game process through the WAL key/value table. */
  public interface Store {
    String get(String key, String fallback);

    void put(String key, String value);
  }

  public static final String KEY_VISIBLE = "timerVisible", KEY_POSITION = "timerPosition";

  private static final Store DATABASE =
      new Store() {
        public String get(String key, String fallback) {
          return HelperApplication.engine().db.get(key, fallback);
        }

        public void put(String key, String value) {
          HelperApplication.engine().db.put(key, value);
        }
      };

  private static final Handler main = new Handler(Looper.getMainLooper());
  private static final ExecutorService io = Executors.newSingleThreadExecutor();
  private static final AtomicInteger latest = new AtomicInteger(-1);
  private static final AtomicBoolean pending = new AtomicBoolean();
  private static volatile Store store = DATABASE;
  private static WeakReference<TextView> current = new WeakReference<>(null);
  // Main-thread state. Position is the pill's top-left as a fraction of the free range (0..1);
  // NaN means the default bottom-center placement.
  private static boolean visible = true;
  private static float fx = Float.NaN, fy = Float.NaN;
  private static int bottomInset;

  private BattleTimerOverlay() {}

  /** Test seam: replace the persisted settings with an isolated store. */
  public static void useStore(Store replacement) {
    store = replacement == null ? DATABASE : replacement;
  }

  public static boolean isEnabled(Store s) {
    return !"0".equals(s.get(KEY_VISIBLE, "1"));
  }

  public static void setEnabled(Store s, boolean enabled) {
    s.put(KEY_VISIBLE, enabled ? "1" : "0");
  }

  public static void resetPosition(Store s) {
    s.put(KEY_POSITION, "");
  }

  public static Store defaultStore() {
    return DATABASE;
  }

  public static void attach(Activity activity) {
    TextView old = current.get();
    if (old != null && old.getContext() == activity) {
      reload();
      return;
    }
    if (old != null && old.getParent() instanceof ViewGroup)
      ((ViewGroup) old.getParent()).removeView(old);
    TextView timer = new TextView(activity);
    timer.setTextColor(0xfff4d79e);
    timer.setTextSize(17);
    timer.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
    timer.setGravity(Gravity.CENTER);
    timer.setIncludeFontPadding(false);
    timer.setMinWidth(dp(timer, 96));
    timer.setPadding(dp(timer, 14), dp(timer, 6), dp(timer, 14), dp(timer, 6));
    timer.setFocusable(false);
    timer.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
    timer.setContentDescription("战斗用时");
    GradientDrawable background = new GradientDrawable();
    background.setColor(0xb8101b23);
    background.setCornerRadius(dp(timer, 10));
    background.setStroke(dp(timer, 1), 0x80e4bd79);
    timer.setBackground(background);
    timer.setVisibility(View.GONE);
    ViewGroup root = (ViewGroup) activity.getWindow().getDecorView();
    root.addView(timer, new FrameLayout.LayoutParams(-2, -2, Gravity.TOP | Gravity.LEFT));
    current = new WeakReference<>(timer);
    timer.setOnApplyWindowInsetsListener(
        (view, insets) -> {
          int bottom;
          if (Build.VERSION.SDK_INT >= 30) {
            bottom =
                insets.getInsetsIgnoringVisibility(
                        WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout())
                    .bottom;
          } else {
            bottom = insets.getStableInsetBottom();
            if (insets.getDisplayCutout() != null)
              bottom = Math.max(bottom, insets.getDisplayCutout().getSafeInsetBottom());
          }
          bottomInset = bottom;
          place(timer);
          return insets;
        });
    // Re-place on any size change of the pill (text width) or the window (rotation).
    timer.addOnLayoutChangeListener((v, l, t, r, b, ol, ot, or, ob) -> place(timer));
    root.addOnLayoutChangeListener((v, l, t, r, b, ol, ot, or, ob) -> place(timer));
    timer.setOnTouchListener(new Drag(timer));
    timer.requestApplyInsets();
    reload();
    render();
  }

  /** Re-read the visibility/position chosen in the helper UI (called when the game resumes). */
  public static void reload() {
    Store s = store;
    io.execute(
        () -> {
          boolean enabled;
          String position;
          try {
            enabled = isEnabled(s);
            position = s.get(KEY_POSITION, "");
          } catch (Exception e) {
            return;
          }
          main.post(
              () -> {
                visible = enabled;
                float[] p = parse(position);
                fx = p[0];
                fy = p[1];
                TextView timer = current.get();
                if (timer != null) place(timer);
                render();
              });
        });
  }

  public static void update(int elapsedMilliseconds) {
    latest.set(Math.max(-1, elapsedMilliseconds));
    // Coalesce accelerated playback callbacks into the latest value; never queue stale frame text.
    if (pending.compareAndSet(false, true))
      main.post(
          () -> {
            TextView timer = current.get();
            if (timer == null) {
              pending.set(false);
              return;
            }
            Choreographer.getInstance()
                .postFrameCallback(
                    frame -> {
                      pending.set(false);
                      render();
                    });
          });
  }

  private static void render() {
    TextView timer = current.get();
    if (timer == null) return;
    int elapsed = latest.get();
    boolean show = visible && elapsed >= 0;
    timer.setVisibility(show ? View.VISIBLE : View.GONE);
    if (show) timer.setText(format(elapsed));
  }

  private static void place(TextView timer) {
    View parent = (View) timer.getParent();
    if (parent == null) return;
    int freeX = Math.max(0, parent.getWidth() - timer.getWidth());
    int freeY = Math.max(0, parent.getHeight() - timer.getHeight());
    if (Float.isNaN(fx) || Float.isNaN(fy)) {
      timer.setTranslationX(freeX / 2f);
      timer.setTranslationY(Math.max(0, freeY - dp(timer, BOTTOM_PADDING_DP) - bottomInset));
    } else {
      timer.setTranslationX(Math.round(clamp(fx) * freeX));
      timer.setTranslationY(Math.round(clamp(fy) * freeY));
    }
  }

  static float[] parse(String value) {
    try {
      String[] parts = value.split(",");
      if (parts.length == 2) {
        float x = Float.parseFloat(parts[0]), y = Float.parseFloat(parts[1]);
        if (!Float.isNaN(x) && !Float.isNaN(y)) return new float[] {clamp(x), clamp(y)};
      }
    } catch (RuntimeException ignored) {
    }
    return new float[] {Float.NaN, Float.NaN};
  }

  private static float clamp(float v) {
    return Math.max(0f, Math.min(1f, v));
  }

  private static final class Drag implements View.OnTouchListener {
    private final TextView timer;
    private final int slop;
    private float downX, downY, startX, startY;
    private boolean dragging;

    Drag(TextView timer) {
      this.timer = timer;
      slop = ViewConfiguration.get(timer.getContext()).getScaledTouchSlop();
    }

    @SuppressLint("ClickableViewAccessibility")
    @Override
    public boolean onTouch(View v, MotionEvent e) {
      View parent = (View) timer.getParent();
      if (parent == null) return false;
      switch (e.getActionMasked()) {
        case MotionEvent.ACTION_DOWN:
          downX = e.getRawX();
          downY = e.getRawY();
          startX = timer.getTranslationX();
          startY = timer.getTranslationY();
          dragging = false;
          return true;
        case MotionEvent.ACTION_MOVE:
          float dx = e.getRawX() - downX, dy = e.getRawY() - downY;
          if (!dragging && Math.hypot(dx, dy) < slop) return true;
          dragging = true;
          int freeX = Math.max(1, parent.getWidth() - timer.getWidth());
          int freeY = Math.max(1, parent.getHeight() - timer.getHeight());
          fx = clamp((startX + dx) / freeX);
          fy = clamp((startY + dy) / freeY);
          place(timer);
          return true;
        case MotionEvent.ACTION_UP:
        case MotionEvent.ACTION_CANCEL:
          if (dragging) {
            String value = String.format(Locale.ROOT, "%.4f,%.4f", fx, fy);
            Store s = store;
            io.execute(
                () -> {
                  try {
                    s.put(KEY_POSITION, value);
                  } catch (Exception ignored) {
                  }
                });
          }
          dragging = false;
          return true;
        default:
          return true;
      }
    }
  }

  public static String format(int elapsed) {
    int centiseconds = Math.max(elapsed, 0) / 10;
    return String.format(
        Locale.ROOT,
        "%d:%02d.%02d",
        centiseconds / 6000,
        centiseconds / 100 % 60,
        centiseconds % 100);
  }

  private static int dp(View view, int value) {
    return Math.round(value * view.getResources().getDisplayMetrics().density);
  }
}
