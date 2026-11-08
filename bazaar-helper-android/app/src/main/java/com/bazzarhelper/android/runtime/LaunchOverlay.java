package com.bazzarhelper.android.runtime;

import android.app.Activity;
import android.content.res.Resources;
import android.graphics.Canvas;
import android.graphics.LinearGradient;
import android.graphics.Paint;
import android.graphics.RectF;
import android.graphics.Shader;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Handler;
import android.os.Looper;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.ViewParent;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.util.Locale;
import java.util.WeakHashMap;

/**
 * Card shown over the game activity while the runtime prepares. The launcher's first start (and
 * every game update) downloads the Unity assemblies, runs Cpp2IL, generates the IL2CPP interop
 * proxies and only then starts the chainloader; the managed side pushes one status line per stage
 * through {@link RuntimeCallbacks}, so this class turns those lines into a centred card with a
 * per-stage estimate, an elapsed clock and a percentage.
 *
 * <p>The interop stage has no measurable progress: the bar walks to {@link #HOLD} and stays there
 * until the stage actually finishes, then runs to 100% and fades out. Palette matches
 * {@code ui.Ui}, which is package-private, so the few colours are repeated here.
 */
final class LaunchOverlay {
  private static final int HOLD = 92;
  /** Index of the interop stage and the elapsed seconds at which it admits running long. */
  private static final int INTEROP_STEP = 3;
  private static final int OVERDUE = 180;

  private static final int SCRIM = 0xcc080f14,
      CARD_TOP = 0xff22353f,
      CARD_BOTTOM = 0xff15232c,
      LINE = 0xff3c5360,
      CHIP = 0xff253a46,
      INK = 0xffecede4,
      MUTED = 0xffa0b2bb,
      ACCENT = 0xffe8b95d,
      TRACK = 0xff2b3f4a;

  /** Status lines the pinned launcher emits, in the order they appear. */
  private static final String[] KEYS = {
    "Running Preloader...",
    "Downloading Unity assemblies...",
    "Running Cpp2IL...",
    "Generating IL2CPP Interop files...",
    "Initialized Chainloader...",
    "Running Chainloader..."
  };

  private static final String[] LABELS = {
    "启动预加载器",
    "下载 Unity 运行依赖",
    "分析游戏程序集",
    "生成游戏代理（IL2CPP 互操作）",
    "初始化插件加载器",
    "加载插件"
  };

  /** Percentage the bar aims at per stage; the interop stage parks at {@link #HOLD}. */
  private static final int[] TARGETS = {6, 20, 55, HOLD, 96, 98};

  /** Rough per-stage duration shown to the user; the clock below counts up, this only sets hope. */
  private static final String[] ESTIMATES = {
    "预计约 10 秒",
    "预计约 1 分钟",
    "预计约 1 分钟",
    "预计约 1–2 分钟",
    "预计约 1 分钟",
    "预计约 1 分钟"
  };

  private static final String PLUGIN_ESTIMATE = "预计约 10 秒";

  private static final int STEPS = KEYS.length + 1;

  private static final WeakHashMap<Activity, LaunchOverlay> SHOWN = new WeakHashMap<>();

  private final Activity activity;
  private final float density;
  private final Handler handler = new Handler(Looper.getMainLooper());
  private final FrameLayout root;
  private final TextView stepChip;
  private final TextView stageText;
  private final TextView estimateText;
  private final TextView percentText;
  private final TextView elapsedText;
  private final Bar bar;
  private float aim = TARGETS[0];
  private float shown;
  private int step;
  private int elapsed;
  private boolean finishing;
  private boolean ticking;

  private final Runnable tick =
      new Runnable() {
        @Override
        public void run() {
          if (activity.isDestroyed()) {
            // The game activity went away mid-preparation: stop the ticks instead of leaking them.
            ticking = false;
            handler.removeCallbacks(this);
            handler.removeCallbacks(clock);
            SHOWN.remove(activity);
            return;
          }
          shown += (aim - shown) * 0.16f;
          if (Math.abs(aim - shown) < 0.4f) shown = aim;
          bar.setFraction(shown / 100f);
          percentText.setText(Math.round(shown) + "%");
          if (finishing && shown >= 100f) {
            handler.postDelayed(LaunchOverlay.this::fadeOut, 700);
            return;
          }
          handler.postDelayed(this, 60);
        }
      };

  private final Runnable clock =
      new Runnable() {
        @Override
        public void run() {
          elapsed++;
          elapsedText.setText(clockText());
          if (step == INTEROP_STEP && elapsed > OVERDUE) estimateText.setText("已超出预计时间，仍在生成，请勿离开");
          handler.postDelayed(this, 1000);
        }
      };

  private LaunchOverlay(Activity activity) {
    this.activity = activity;
    density = activity.getResources().getDisplayMetrics().density;

    root = new FrameLayout(activity);
    root.setBackgroundColor(SCRIM);
    root.setClickable(false);
    root.setFocusable(false);
    root.setAlpha(0f);

    LinearLayout card = new LinearLayout(activity);
    card.setOrientation(LinearLayout.VERTICAL);
    card.setGravity(Gravity.CENTER_HORIZONTAL);
    GradientDrawable background =
        new GradientDrawable(
            GradientDrawable.Orientation.TOP_BOTTOM, new int[] {CARD_TOP, CARD_BOTTOM});
    background.setCornerRadius(dp(18));
    background.setStroke(Math.max(1, dp(1)), LINE);
    card.setBackground(background);
    card.setPadding(dp(24), dp(22), dp(24), dp(22));

    stepChip = label(11, ACCENT, Typeface.DEFAULT_BOLD);
    GradientDrawable pill = new GradientDrawable();
    pill.setColor(CHIP);
    pill.setCornerRadius(dp(99));
    stepChip.setBackground(pill);
    stepChip.setPadding(dp(10), dp(4), dp(10), dp(4));
    stepChip.setText(stepLabel(step));
    card.addView(stepChip, stacked(0, 0));

    TextView title = label(19, INK, Typeface.DEFAULT_BOLD);
    title.setText("正在准备游戏环境");
    card.addView(title, stacked(12, 0));

    stageText = label(14, MUTED, Typeface.DEFAULT);
    stageText.setGravity(Gravity.CENTER);
    stageText.setText("正在初始化…");
    card.addView(stageText, stacked(8, 0));

    estimateText = label(12, MUTED, Typeface.DEFAULT);
    estimateText.setGravity(Gravity.CENTER);
    estimateText.setText(ESTIMATES[0]);
    card.addView(estimateText, stacked(6, 0));

    bar = new Bar(activity, dp(4));
    LinearLayout.LayoutParams barParams = new LinearLayout.LayoutParams(-1, dp(8));
    barParams.setMargins(0, dp(18), 0, 0);
    card.addView(bar, barParams);

    LinearLayout row = new LinearLayout(activity);
    row.setOrientation(LinearLayout.HORIZONTAL);
    row.setGravity(Gravity.CENTER_VERTICAL);
    percentText = label(13, ACCENT, Typeface.DEFAULT_BOLD);
    percentText.setText("0%");
    row.addView(percentText, new LinearLayout.LayoutParams(0, -2, 1f));
    elapsedText = label(12, MUTED, Typeface.DEFAULT);
    elapsedText.setGravity(Gravity.END);
    elapsedText.setText(clockText());
    row.addView(elapsedText, new LinearLayout.LayoutParams(-2, -2));
    card.addView(row, wide(8, 0));

    TextView reminder = label(12, MUTED, Typeface.DEFAULT);
    reminder.setLineSpacing(dp(3), 1f);
    reminder.setText(
        "首次启动或游戏更新后需要生成代理。请留在此页面并保持前台，不要锁屏或切到后台；"
            + "提前离开会重新开始这一步。");
    card.addView(reminder, wide(18, 0));

    FrameLayout.LayoutParams params =
        new FrameLayout.LayoutParams(
            Math.min(dp(344), Math.max(dp(240), width() - dp(48))), -2);
    params.gravity = Gravity.CENTER;
    root.addView(card, params);
  }

  static void show(Activity activity) {
    if (activity == null || activity.isDestroyed()) return;
    onUi(
        activity,
        () -> {
          LaunchOverlay overlay = SHOWN.get(activity);
          if (overlay == null) {
            overlay = new LaunchOverlay(activity);
            SHOWN.put(activity, overlay);
            overlay.attach();
          }
          overlay.reveal();
        });
  }

  static void text(Activity activity, String message) {
    if (activity == null || message == null) return;
    onUi(
        activity,
        () -> {
          LaunchOverlay overlay = SHOWN.get(activity);
          if (overlay != null) overlay.stage(message);
        });
  }

  static void hide(Activity activity) {
    if (activity == null) return;
    onUi(
        activity,
        () -> {
          LaunchOverlay overlay = SHOWN.get(activity);
          if (overlay != null) overlay.finish();
        });
  }

  private static void onUi(Activity activity, Runnable run) {
    if (Looper.myLooper() == Looper.getMainLooper()) run.run();
    else activity.runOnUiThread(run);
  }

  private void attach() {
    ViewGroup decor = (ViewGroup) activity.getWindow().getDecorView();
    decor.addView(root, new ViewGroup.LayoutParams(-1, -1));
  }

  private void reveal() {
    if (root.getParent() == null) attach();
    finishing = false;
    root.setVisibility(View.VISIBLE);
    root.animate().cancel();
    root.animate().alpha(1f).setDuration(180).start();
    if (!ticking) {
      ticking = true;
      handler.post(tick);
      handler.postDelayed(clock, 1000);
    }
  }

  /** Runs the bar to 100%, then fades the card out; the game keeps drawing underneath. */
  private void finish() {
    if (finishing) return;
    finishing = true;
    aim = 100;
    handler.removeCallbacks(clock);
    estimateText.setText("已完成");
    elapsedText.setText(clockText());
  }

  private void fadeOut() {
    ticking = false;
    handler.removeCallbacks(tick);
    handler.removeCallbacks(clock);
    SHOWN.remove(activity);
    root.animate()
        .alpha(0f)
        .setDuration(220)
        .withEndAction(
            () -> {
              ViewParent parent = root.getParent();
              if (parent instanceof ViewGroup) ((ViewGroup) parent).removeView(root);
            })
        .start();
  }

  private void stage(String message) {
    int index = -1;
    for (int i = 0; i < KEYS.length; i++)
      if (message.equals(KEYS[i])) {
        index = i;
        break;
      }
    String text;
    String estimate;
    int target;
    if (index >= 0) {
      text = LABELS[index];
      estimate = ESTIMATES[index];
      target = TARGETS[index];
      step = index;
    } else if (message.startsWith("Loading ")) {
      String name = message.substring("Loading ".length()).replaceAll("\\.+$", "");
      text = name.isEmpty() ? "加载插件" : "加载插件 " + name;
      estimate = PLUGIN_ESTIMATE;
      target = 100;
      step = KEYS.length;
    } else {
      text = message;
      estimate = "正在处理…";
      target = (int) Math.max(aim, HOLD);
    }
    stageText.setText(text);
    estimateText.setText(estimate);
    stepChip.setText(stepLabel(step));
    aim = target;
    if (shown > aim) shown = aim;
    elapsed = 0;
    elapsedText.setText(clockText());
  }

  private String clockText() {
    return String.format(Locale.ROOT, "%02d:%02d", elapsed / 60, elapsed % 60);
  }

  private static String stepLabel(int step) {
    return "第 " + Math.min(step + 1, STEPS) + " / " + STEPS + " 步";
  }

  private TextView label(int sp, int color, Typeface face) {
    TextView view = new TextView(activity);
    view.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
    view.setTextColor(color);
    view.setTypeface(face);
    view.setIncludeFontPadding(false);
    return view;
  }

  private int width() {
    Resources resources = activity.getResources();
    return resources.getDisplayMetrics().widthPixels;
  }

  private int dp(float value) {
    return Math.round(value * density);
  }

  private LinearLayout.LayoutParams stacked(int top, int bottom) {
    LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-2, -2);
    params.setMargins(0, dp(top), 0, dp(bottom));
    return params;
  }

  private LinearLayout.LayoutParams wide(int top, int bottom) {
    LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
    params.setMargins(0, dp(top), 0, dp(bottom));
    return params;
  }

  /** Rounded track whose gold fill eases towards the current stage's target. */
  private static final class Bar extends View {
    private final Paint track = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint fill = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF rect = new RectF();
    private final int radius;
    private float fraction;

    Bar(Activity activity, int radius) {
      super(activity);
      this.radius = radius;
      track.setColor(TRACK);
      setWillNotDraw(false);
    }

    void setFraction(float value) {
      fraction = Math.max(0f, Math.min(1f, value));
      invalidate();
    }

    @Override
    protected void onSizeChanged(int w, int h, int oldw, int oldh) {
      fill.setShader(
          new LinearGradient(0, 0, Math.max(1, w), h, ACCENT, 0xfff2d98c, Shader.TileMode.CLAMP));
    }

    @Override
    protected void onDraw(Canvas canvas) {
      float round = Math.min(radius, getHeight() / 2f);
      rect.set(0, 0, getWidth(), getHeight());
      canvas.drawRoundRect(rect, round, round, track);
      if (fraction <= 0f) return;
      rect.set(0, 0, Math.max(getHeight(), getWidth() * fraction), getHeight());
      canvas.drawRoundRect(rect, round, round, fill);
    }
  }
}
