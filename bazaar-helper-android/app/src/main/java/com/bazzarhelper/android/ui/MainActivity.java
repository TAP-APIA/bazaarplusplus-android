package com.bazzarhelper.android.ui;

import android.app.Activity;
import android.app.ActivityManager;
import android.content.*;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.net.*;
import android.os.*;
import android.provider.MediaStore;
import android.util.Base64;
import android.view.*;
import android.widget.*;
import com.bazzarhelper.android.*;
import com.bazzarhelper.android.runtime.*;
import com.bazzarhelper.android.sync.*;
import java.time.*;
import java.time.format.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.function.Consumer;
import org.json.*;

/**
 * Sign-in gate and helper home. Nothing but the sign-in screen is reachable until a WeChat account
 * owns this device, because every record is attributed to that account at settlement.
 */
public final class MainActivity extends Activity {

  private final ExecutorService io = Executors.newSingleThreadExecutor();
  private final Handler handler = new Handler(Looper.getMainLooper());

  private String screen = "";
  private TextView accountName, accountState, accountPill, gameState, recordPill, syncState,
      loginTitle, loginBody, loginHint, loginCancel;
  private ChunkyButton accountAction, launch, resetPosition, historyButton, syncButton, qrLogin,
      exportButton;
  private Switch timerSwitch;
  private View gameDot;
  private Sheet qrSheet, progressSheet;
  private Bitmap qr;
  private boolean active, launching, syncing, bindingTimer, working, pending;

  private SyncEngine engine() {
    return HelperApplication.engine();
  }

  @Override
  public void onCreate(Bundle b) {
    super.onCreate(b);
    Window window = getWindow();
    window.setStatusBarColor(Ui.BG);
    window.setNavigationBarColor(Ui.BG);
    window.getDecorView().setSystemUiVisibility(0);
    route();
  }

  @Override
  protected void onResume() {
    super.onResume();
    active = true;
    route();
    handler.postDelayed(tick, 2500);
  }

  @Override
  protected void onPause() {
    active = false;
    handler.removeCallbacks(tick);
    super.onPause();
  }

  @Override
  protected void onDestroy() {
    handler.removeCallbacks(tick);
    io.shutdown();
    super.onDestroy();
  }

  private final Runnable tick =
      new Runnable() {
        public void run() {
          if (!active) return;
          io.execute(
              () -> {
                boolean confirmed = false;
                try {
                  if (pending && engine().poll()) confirmed = true;
                } catch (Exception ignored) {
                }
                final boolean boundNow = confirmed;
                runOnUiThread(
                    () -> {
                      if (boundNow) {
                        pending = false;
                        qr = null;
                        if (qrSheet != null) qrSheet.dismiss();
                        Diag.i("login", "confirmed");
                        toast("微信绑定完成");
                        route();
                      } else if ("home".equals(screen)) {
                        refresh();
                      }
                    });
                if (!boundNow && "login".equals(screen)) runOnUiThread(MainActivity.this::refreshLogin);
              });
          handler.postDelayed(this, 2500);
        }
      };

  // ---- routing ----

  private JSONObject session() {
    try {
      return engine().db.session();
    } catch (Exception e) {
      return null;
    }
  }

  private boolean bound() {
    JSONObject s = session();
    return s != null && !s.optString("owner").isEmpty();
  }

  private boolean awaitingConfirm() {
    JSONObject s = session();
    return s != null && !s.optString("token").isEmpty() && s.optString("owner").isEmpty();
  }

  private void route() {
    String next = bound() ? "home" : "login";
    if (!next.equals(screen)) {
      screen = next;
      Diag.i("ui", "screen " + next);
      pending = awaitingConfirm();
      if ("home".equals(next)) buildHome();
      else buildLogin();
    }
    if ("home".equals(screen)) refresh();
    else refreshLogin();
  }

  // ---- shared layout ----

  private ScrollView shell() {
    ScrollView scroll = new ScrollView(this);
    scroll.setBackgroundColor(Ui.BG);
    scroll.setFillViewport(true);
    scroll.setClipToPadding(false);
    return scroll;
  }

  private LinearLayout page(ScrollView scroll) {
    LinearLayout page = Ui.column(this);
    page.setPadding(Ui.dp(18), Ui.dp(12), Ui.dp(18), Ui.dp(28));
    scroll.addView(page, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, -2));
    setContentView(scroll);
    return page;
  }

  private static Bitmap brand;

  /** Launcher artwork, cropped to the badge inside the adaptive-icon canvas. */
  private Bitmap brandBitmap() {
    if (brand != null) return brand;
    try {
      int id = getResources().getIdentifier("ic_launcher_foreground", "mipmap", getPackageName());
      if (id == 0) return null;
      Bitmap full = BitmapFactory.decodeResource(getResources(), id);
      if (full == null) return null;
      int side = Math.round(full.getWidth() * 0.82f);
      brand =
          Bitmap.createBitmap(
              full, (full.getWidth() - side) / 2, (full.getHeight() - side) / 2, side, side);
      return brand;
    } catch (Exception e) {
      Diag.e("ui", "brand icon decode failed", e);
      return null;
    }
  }

  private String versionName() {
    try {
      return getPackageManager().getPackageInfo(getPackageName(), 0).versionName;
    } catch (Exception e) {
      return "";
    }
  }

  private View header() {
    LinearLayout row = Ui.row(this);
    int markSize = Ui.dp(46);
    Bitmap badge = brandBitmap();
    View mark;
    if (badge != null) {
      ImageView icon = new ImageView(this);
      icon.setImageBitmap(badge);
      icon.setContentDescription("大巴扎助手");
      mark = icon;
    } else {
      TextView letter = Ui.title(this, "巴", 19, Ui.ON_ACCENT);
      letter.setGravity(Gravity.CENTER);
      letter.setBackground(new Ui.Depth(Ui.ACCENT, Ui.ALPHA_NONE, Ui.edge(Ui.ACCENT), 15, 3, 0));
      letter.setPadding(0, 0, 0, Ui.dp(3));
      mark = letter;
    }
    LinearLayout.LayoutParams markParams = new LinearLayout.LayoutParams(markSize, markSize);
    markParams.rightMargin = Ui.dp(12);
    row.addView(mark, markParams);

    LinearLayout titles = Ui.column(this);
    titles.addView(Ui.eyebrow(this, "BAZAAR HELPER"));
    titles.addView(Ui.spacer(this, 3));
    titles.addView(Ui.title(this, "大巴扎助手", 26, Ui.INK));
    row.addView(titles, new LinearLayout.LayoutParams(0, -2, 1));
    String version = versionName();
    row.addView(Ui.pill(this, version.isEmpty() ? "—" : "v" + version, Ui.ACCENT));

    LinearLayout header = Ui.column(this);
    header.setPadding(0, Ui.dp(2), 0, Ui.dp(16));
    header.addView(row);
    return header;
  }

  // ---- sign-in ----

  private void buildLogin() {
    ScrollView scroll = shell();
    LinearLayout page = page(scroll);
    page.addView(header());

    LinearLayout card = Ui.card(this);
    card.addView(Ui.eyebrow(this, "开始使用"));
    card.addView(Ui.spacer(this, 10));
    loginTitle = Ui.title(this, "登录微信账号", 20, Ui.INK);
    card.addView(loginTitle);
    LinearLayout.LayoutParams bodyParams = new LinearLayout.LayoutParams(-1, -2);
    bodyParams.topMargin = Ui.dp(8);
    loginBody = Ui.body(this, "", Ui.MUTED);
    card.addView(loginBody, bodyParams);

    qrLogin = ChunkyButton.primary(this).label("微信扫码登录");
    qrLogin.setOnClickListener(v -> loginQr());
    LinearLayout.LayoutParams qrParams = new LinearLayout.LayoutParams(-1, -2);
    qrParams.topMargin = Ui.dp(18);
    card.addView(qrLogin, qrParams);

    LinearLayout.LayoutParams hintParams = new LinearLayout.LayoutParams(-1, -2);
    hintParams.topMargin = Ui.dp(14);
    loginHint = Ui.muted(this, "");
    card.addView(loginHint, hintParams);

    loginCancel = Ui.text(this, "取消这次登录", 12.5f, Ui.ACCENT, Ui.strong());
    loginCancel.setPadding(0, Ui.dp(10), 0, Ui.dp(2));
    Ui.pressable(loginCancel);
    loginCancel.setOnClickListener(v -> cancelLogin());
    LinearLayout.LayoutParams cancelParams = new LinearLayout.LayoutParams(-1, -2);
    cancelParams.topMargin = Ui.dp(4);
    card.addView(loginCancel, cancelParams);
    Ui.cardIn(page, card);

    LinearLayout benefits = Ui.column(this);
    benefits.setPadding(Ui.dp(6), 0, 0, Ui.dp(10));
    for (String line :
        new String[] {"战绩只属于这个微信账号", "新战绩自动同步到小程序", "随时可在账号管理里退出"}) {
      LinearLayout row = Ui.row(this);
      View dot = new View(this);
      dot.setBackground(Ui.round(Ui.ACCENT, 4));
      LinearLayout.LayoutParams dotParams = new LinearLayout.LayoutParams(Ui.dp(6), Ui.dp(6));
      row.addView(dot, dotParams);
      TextView label = Ui.text(this, line, 12.5f, Ui.MUTED, Ui.strong());
      LinearLayout.LayoutParams labelParams = new LinearLayout.LayoutParams(0, -2, 1);
      labelParams.leftMargin = Ui.dp(10);
      row.addView(label, labelParams);
      LinearLayout.LayoutParams rowParams = new LinearLayout.LayoutParams(-1, -2);
      rowParams.topMargin = Ui.dp(10);
      benefits.addView(row, rowParams);
    }
    page.addView(benefits);

    page.addView(footer());
    refreshLogin();
  }

  private void refreshLogin() {
    if (!"login".equals(screen) || loginTitle == null) return;
    boolean waiting = pending || awaitingConfirm();
    loginTitle.setText(waiting ? "等待微信确认" : "登录微信账号");
    loginBody.setText(
        waiting
            ? "在微信里完成确认后会自动进入助手。小程序码 5 分钟内有效。"
            : "战绩只属于绑定的微信账号，绑定后自动同步到小程序。");
    qrLogin.label(waiting ? (qr == null ? "重新获取二维码" : "显示二维码") : "微信扫码登录");
    loginCancel.setVisibility(waiting ? View.VISIBLE : View.GONE);
    loginHint.setText(
        waiting
            ? "没有看到微信页面？点「显示二维码」保存后用微信「扫一扫 → 相册」识别。"
            : "生成的小程序码可存到相册，再用微信「扫一扫 → 右上角相册」识别。");
  }

  private void cancelLogin() {
    io.execute(
        () -> {
          try {
            engine().abandonLogin();
          } catch (Exception ignored) {
          }
          runOnUiThread(
              () -> {
                pending = false;
                qr = null;
                refreshLogin();
              });
        });
  }

  private void loginQr() {
    if (working) return;
    if (pending && qr != null) {
      showQrSheet();
      return;
    }
    requestSession(
        result -> {
          pending = true;
          qr = decode(result.optString("qrCode", ""));
          refreshLogin();
          if (qr == null) error("小程序码解析失败，请重试");
          else showQrSheet();
        },
        "正在生成小程序码");
  }

  private void requestSession(Consumer<JSONObject> onReady, String title) {
    Diag.i("login", "request session · " + title);
    working = true;
    setLoginBusy(true);
    showProgress(title, "首次登录需要向服务器申请一次性凭据，请稍候。");
    io.execute(
        () -> {
          JSONObject result = null;
          String failure = null;
          try {
            JSONObject current = engine().db.session();
            if (!current.optString("owner").isEmpty()) throw new IllegalStateException("已绑定");
            if (!current.optString("token").isEmpty()) engine().abandonLogin();
            result = engine().login();
          } catch (Exception e) {
            Diag.e("login", "session failed", e);
            failure = describe(e);
          }
          final JSONObject ready = result;
          final String message = failure;
          runOnUiThread(
              () -> {
                working = false;
                setLoginBusy(false);
                dismissProgress();
                if (ready == null) {
                  error(message == null ? "登录未完成，请重试" : message);
                  return;
                }
                Diag.i(
                    "login",
                    "session ready · qr " + (ready.optString("qrCode").isEmpty() ? "missing" : "present"));
                onReady.accept(ready);
              });
        });
  }

  private void setLoginBusy(boolean busy) {
    if (qrLogin == null) return;
    qrLogin.setEnabled(!busy);
  }

  private void showProgress(String title, String message) {
    dismissProgress();
    ProgressBar bar = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
    bar.setIndeterminate(true);
    bar.setIndeterminateTintList(android.content.res.ColorStateList.valueOf(Ui.ACCENT));
    bar.setLayoutParams(new LinearLayout.LayoutParams(-1, Ui.dp(6)));
    progressSheet =
        new Sheet(this, "登录微信账号", title).message(message).body(bar).cancelable(false);
    progressSheet.show();
  }

  private void dismissProgress() {
    if (progressSheet != null) {
      progressSheet.dismiss();
      progressSheet = null;
    }
  }

  private Bitmap decode(String dataUrl) {
    try {
      if (!dataUrl.startsWith("data:image/")) return null;
      byte[] bytes = Base64.decode(dataUrl.substring(dataUrl.indexOf(',') + 1), Base64.DEFAULT);
      return BitmapFactory.decodeByteArray(bytes, 0, bytes.length);
    } catch (Exception e) {
      return null;
    }
  }

  private void showQrSheet() {
    if (qrSheet != null) qrSheet.dismiss();
    ImageView image = new ImageView(this);
    image.setImageBitmap(qr);
    image.setBackgroundColor(0xffffffff);
    image.setLayoutParams(new LinearLayout.LayoutParams(-1, Ui.dp(300)));
    qrSheet =
        new Sheet(this, "微信扫码登录", "用微信识别小程序码")
            .message("点「保存到相册」，再到微信「扫一扫 → 右上角相册」选择刚保存的图片；也可以直接用另一台设备扫码。")
            .body(image)
            .primaryStaysOpen("保存到相册", this::saveQrToAlbum)
            .secondary("关闭", null);
    qrSheet.show();
  }

  private void saveQrToAlbum() {
    if (qr == null) {
      error("小程序码已失效，请重新获取");
      return;
    }
    io.execute(
        () -> {
          String failure = null;
          try {
            ContentValues v = new ContentValues();
            v.put(
                MediaStore.Images.Media.DISPLAY_NAME,
                "BazaarHelper-login-" + System.currentTimeMillis() + ".png");
            v.put(MediaStore.Images.Media.MIME_TYPE, "image/png");
            v.put(MediaStore.Images.Media.RELATIVE_PATH, "Pictures/BazaarHelper");
            v.put(MediaStore.Images.Media.IS_PENDING, 1);
            Uri uri =
                getContentResolver().insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, v);
            if (uri == null) throw new Exception();
            try (java.io.OutputStream out = getContentResolver().openOutputStream(uri)) {
              qr.compress(Bitmap.CompressFormat.PNG, 100, out);
            }
            v.clear();
            v.put(MediaStore.Images.Media.IS_PENDING, 0);
            getContentResolver().update(uri, v, null, null);
          } catch (Exception e) {
            failure = "保存失败，请检查相册权限";
          }
          final String message = failure;
          runOnUiThread(
              () -> {
                if (message != null) {
                  error(message);
                  return;
                }
                toast("已保存到相册，可用微信「扫一扫 → 相册」识别");
              });
        });
  }

  // ---- home ----

  private void buildHome() {
    ScrollView scroll = shell();
    LinearLayout page = page(scroll);
    page.addView(header());
    Ui.cardIn(page, accountCard());
    Ui.cardIn(page, gameCard());
    Ui.cardIn(page, timerCard());
    Ui.cardIn(page, recordsCard());
    exportButton = ChunkyButton.secondary(this).label("导出日志").icon(Ui.Glyph.EXPORT);
    exportButton.setOnClickListener(v -> exportLogs());
    LinearLayout.LayoutParams exportParams = new LinearLayout.LayoutParams(-1, -2);
    page.addView(exportButton, exportParams);
    page.addView(footer());
    bindTimerSwitch();
  }

  private LinearLayout accountCard() {
    LinearLayout card = Ui.card(this);
    card.addView(Ui.eyebrow(this, "账号"));
    card.addView(Ui.spacer(this, 12));

    LinearLayout head = Ui.row(this);
    head.addView(Ui.tile(this, Ui.Glyph.ACCOUNT, 40, Ui.HERO, Ui.ACCENT));
    LinearLayout names = Ui.column(this);
    names.setPadding(Ui.dp(12), 0, Ui.dp(8), 0);
    accountName = Ui.title(this, "正在读取账号…", 16.5f, Ui.INK);
    accountState = Ui.muted(this, "扫码绑定后自动同步");
    names.addView(accountName);
    names.addView(Ui.spacer(this, 4));
    names.addView(accountState);
    head.addView(names, new LinearLayout.LayoutParams(0, -2, 1));
    accountPill = Ui.pill(this, "未绑定", Ui.MUTED);
    head.addView(accountPill);
    card.addView(head);

    accountAction = ChunkyButton.primary(this).label("账号管理").icon(Ui.Glyph.ACCOUNT);
    LinearLayout.LayoutParams actionParams = new LinearLayout.LayoutParams(-1, -2);
    actionParams.topMargin = Ui.dp(16);
    accountAction.setOnClickListener(v -> accountSheet());
    card.addView(accountAction, actionParams);
    return card;
  }

  private LinearLayout gameCard() {
    LinearLayout card = Ui.card(this);
    card.addView(Ui.eyebrow(this, "开始记录"));
    card.addView(Ui.spacer(this, 12));

    LinearLayout head = Ui.row(this);
    head.addView(Ui.tile(this, Ui.Glyph.PLAY, 40, Ui.RAISED, Ui.ACCENT));
    LinearLayout titles = Ui.column(this);
    titles.setPadding(Ui.dp(12), 0, 0, 0);
    titles.addView(Ui.title(this, "从助手启动游戏", 16.5f, Ui.INK));
    titles.addView(Ui.spacer(this, 4));
    titles.addView(Ui.muted(this, "自动记录双方棋盘、结算与战斗指标"));
    head.addView(titles, new LinearLayout.LayoutParams(0, -2, 1));
    card.addView(head);

    launch = ChunkyButton.primary(this).label("启动游戏").icon(Ui.Glyph.PLAY);
    LinearLayout.LayoutParams launchParams = new LinearLayout.LayoutParams(-1, -2);
    launchParams.topMargin = Ui.dp(16);
    launch.setOnClickListener(v -> launch());
    card.addView(launch, launchParams);

    LinearLayout status = Ui.row(this);
    status.setPadding(0, Ui.dp(12), 0, 0);
    gameDot = new View(this);
    LinearLayout.LayoutParams dotParams = new LinearLayout.LayoutParams(Ui.dp(8), Ui.dp(8));
    dotParams.rightMargin = Ui.dp(8);
    gameDot.setBackground(Ui.round(Ui.MUTED, 4));
    status.addView(gameDot, dotParams);
    gameState = Ui.text(this, "从助手启动游戏后启用采集", 12.5f, Ui.MUTED, Ui.strong());
    status.addView(gameState, new LinearLayout.LayoutParams(0, -2, 1));
    card.addView(status);
    return card;
  }

  private LinearLayout timerCard() {
    LinearLayout card = Ui.card(this);
    card.addView(Ui.eyebrow(this, "计时器"));
    card.addView(Ui.spacer(this, 12));

    LinearLayout head = Ui.row(this);
    head.addView(Ui.tile(this, Ui.Glyph.CLOCK, 40, Ui.RAISED, Ui.ACCENT));
    LinearLayout titles = Ui.column(this);
    titles.setPadding(Ui.dp(12), 0, 0, 0);
    titles.addView(Ui.title(this, "悬浮计时器", 16.5f, Ui.INK));
    titles.addView(Ui.spacer(this, 4));
    titles.addView(Ui.muted(this, "战斗中显示用时，游戏中可拖动"));
    head.addView(titles, new LinearLayout.LayoutParams(0, -2, 1));
    card.addView(head);

    card.addView(Ui.divider(this));

    LinearLayout toggleRow = Ui.row(this);
    LinearLayout toggleText = Ui.column(this);
    toggleText.addView(Ui.text(this, "显示计时器", 15, Ui.INK, Ui.strong()));
    toggleText.addView(Ui.spacer(this, 3));
    toggleText.addView(Ui.muted(this, "只影响显示，不影响采集"));
    toggleRow.addView(toggleText, new LinearLayout.LayoutParams(0, -2, 1));
    timerSwitch = new Switch(this);
    timerSwitch.setContentDescription("显示计时器");
    timerSwitch.setThumbTintList(android.content.res.ColorStateList.valueOf(Ui.ACCENT));
    timerSwitch.setTrackTintList(android.content.res.ColorStateList.valueOf(Ui.CHIP));
    toggleRow.addView(timerSwitch);
    card.addView(toggleRow);

    resetPosition = ChunkyButton.secondary(this).label("恢复默认位置").icon(Ui.Glyph.CLOCK);
    LinearLayout.LayoutParams resetParams = new LinearLayout.LayoutParams(-1, -2);
    resetParams.topMargin = Ui.dp(14);
    resetPosition.setOnClickListener(v -> resetTimerPosition());
    card.addView(resetPosition, resetParams);
    return card;
  }

  private LinearLayout recordsCard() {
    LinearLayout card = Ui.card(this);
    card.addView(Ui.eyebrow(this, "战绩"));
    card.addView(Ui.spacer(this, 12));

    LinearLayout head = Ui.row(this);
    head.addView(Ui.tile(this, Ui.Glyph.RECORDS, 40, Ui.RAISED, Ui.ACCENT));
    LinearLayout titles = Ui.column(this);
    titles.setPadding(Ui.dp(12), 0, Ui.dp(8), 0);
    titles.addView(Ui.title(this, "本地战绩", 16.5f, Ui.INK));
    titles.addView(Ui.spacer(this, 4));
    syncState = Ui.muted(this, "尚未同步");
    titles.addView(syncState);
    head.addView(titles, new LinearLayout.LayoutParams(0, -2, 1));
    recordPill = Ui.pill(this, "0 场", Ui.BODY);
    head.addView(recordPill);
    card.addView(head);

    LinearLayout actions = Ui.row(this);
    LinearLayout.LayoutParams actionsParams = new LinearLayout.LayoutParams(-1, -2);
    actionsParams.topMargin = Ui.dp(16);
    historyButton = ChunkyButton.secondary(this).label("查看战绩");
    historyButton.setOnClickListener(v -> history());
    LinearLayout.LayoutParams leftParams = new LinearLayout.LayoutParams(0, -2, 1);
    leftParams.rightMargin = Ui.dp(6);
    actions.addView(historyButton, leftParams);
    syncButton = ChunkyButton.secondary(this).label("立即同步").icon(Ui.Glyph.SYNC);
    syncButton.setOnClickListener(v -> syncNow());
    LinearLayout.LayoutParams rightParams = new LinearLayout.LayoutParams(0, -2, 1);
    rightParams.leftMargin = Ui.dp(6);
    actions.addView(syncButton, rightParams);
    card.addView(actions, actionsParams);
    return card;
  }

  private View footer() {
    TextView note = Ui.text(this, "只读采集 · 不修改游戏速度或结算", 11, Ui.MUTED, Ui.strong());
    note.setGravity(Gravity.CENTER_HORIZONTAL);
    LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(-1, -2);
    p.topMargin = Ui.dp(6);
    note.setLayoutParams(p);
    return note;
  }

  // ---- home state ----

  private void bindTimerSwitch() {
    timerSwitch.setOnCheckedChangeListener(
        (v, checked) -> {
          if (bindingTimer) return;
          Diag.i("timer", "visible=" + checked);
          io.execute(
              () -> {
                try {
                  BattleTimerOverlay.setEnabled(BattleTimerOverlay.defaultStore(), checked);
                } catch (Exception ignored) {
                }
              });
        });
    io.execute(
        () -> {
          boolean enabled = true;
          try {
            enabled = BattleTimerOverlay.isEnabled(BattleTimerOverlay.defaultStore());
          } catch (Exception ignored) {
          }
          final boolean value = enabled;
          runOnUiThread(
              () -> {
                if (timerSwitch == null) return;
                bindingTimer = true;
                timerSwitch.setChecked(value);
                bindingTimer = false;
              });
        });
  }

  private void refresh() {
    io.execute(
        () -> {
          String alias = "", owner = "", sync = "", hook = "";
          int records = 0;
          boolean failed = false;
          try {
            JSONObject s = engine().db.session();
            alias = s.optString("alias");
            owner = s.optString("owner");
            sync = engine().db.get("syncStatus", "");
            hook = engine().db.get("hookStatus", "");
            records = owner.isEmpty() ? 0 : engine().db.records(owner, null).size();
          } catch (Exception e) {
            failed = true;
          }
          boolean running = false;
          ActivityManager manager = getSystemService(ActivityManager.class);
          List<ActivityManager.RunningAppProcessInfo> processes = manager.getRunningAppProcesses();
          if (processes != null)
            for (ActivityManager.RunningAppProcessInfo process : processes)
              if ((getPackageName() + ":game").equals(process.processName)) running = true;

          final String fAlias = alias,
              fOwner = owner,
              fSync = sync,
              fHook = hook;
          final int fRecords = records;
          final boolean fRunning = running, fFailed = failed;
          runOnUiThread(
              () -> {
                if (isFinishing() || !"home".equals(screen)) return;
                apply(fAlias, fOwner, fSync, fHook, fRecords, fRunning, fFailed);
              });
        });
  }

  private void apply(
      String alias,
      String owner,
      String sync,
      String hook,
      int records,
      boolean running,
      boolean failed) {
    if (failed || owner.isEmpty()) {
      accountName.setText("正在确认账号…");
      accountState.setText("读取账号信息");
      setPill(accountPill, "确认中", Ui.MUTED);
    } else {
      accountName.setText(alias.isEmpty() ? "微信账号" : alias);
      accountState.setText("新战绩会自动同步到小程序");
      setPill(accountPill, "已绑定", Ui.WIN);
    }
    recordPill.setText(records + " 场");
    syncState.setText(syncText(sync));
    gameState.setText(running ? (hook.isEmpty() ? "采集中" : hook) : "从助手启动游戏后启用采集");
    gameState.setTextColor(running ? Ui.WIN : Ui.MUTED);
    gameDot.setBackground(Ui.round(running ? Ui.WIN : Ui.MUTED, 4));
  }

  private static String syncText(String value) {
    if (value == null || value.isEmpty()) return "尚未同步";
    String prefix = "同步完成 · ";
    if (value.startsWith(prefix)) {
      try {
        LocalDateTime at =
            Instant.parse(value.substring(prefix.length()))
                .atZone(ZoneId.systemDefault())
                .toLocalDateTime();
        return "上次同步 " + at.format(DateTimeFormatter.ofPattern("M月d日 HH:mm"));
      } catch (Exception ignored) {
        return "同步完成";
      }
    }
    return value;
  }

  private void setPill(TextView pill, String text, int color) {
    pill.setText(text);
    pill.setTextColor(color);
  }

  // ---- home actions ----

  private void accountSheet() {
    JSONObject session = session();
    String owner = session == null ? "" : session.optString("owner");
    String alias = session == null ? "" : session.optString("alias", "微信账号");
    new Sheet(
            this, "账号", owner.isEmpty() ? "等待扫码确认" : "已绑定 " + alias)
        .message("退出后本机历史战绩仍保留；再次登录同一微信账号即可看到，其他账号不能认领。")
        .danger("退出 / 重新绑定", this::logout)
        .secondary("关闭", null)
        .show();
  }

  private void logout() {
    io.execute(
        () -> {
          String message;
          try {
            engine().logout();
            HelperApplication.wake();
            Diag.i("account", "logged out");
            message = "已退出当前微信账号";
          } catch (Exception e) {
            message = "退出失败，请重试";
          }
          final String result = message;
          runOnUiThread(
              () -> {
                qr = null;
                pending = false;
                toast(result);
                screen = "";
                route();
              });
        });
  }

  private void launch() {
    if (launching) return;
    RuntimeInstaller.Preview preview = RuntimeInstaller.preview(this);
    if (!preview.needed()) {
      beginLaunch();
      return;
    }
    if (preview.downloads() && !online()) {
      Diag.i("game", "launch blocked: offline");
      new Sheet(this, "启动游戏", "需要联网准备运行环境")
          .message(
              "首次启动或游戏更新后，助手要下载 Unity 代理依赖（几 MB）才能运行。\n请先连接 Wi-Fi 或移动网络再启动。")
          .primary("知道了", null)
          .show();
      return;
    }
    StringBuilder body = new StringBuilder("首次启动或游戏更新后，助手要先准备运行环境：\n");
    if (preview.downloads()) body.append("· 下载 Unity 代理依赖，几 MB，会消耗流量\n");
    else if (preview.bundled) body.append("· 从安装包解出 Unity 代理依赖，无需联网\n");
    if (preview.unpackRuntime) body.append("· 解包 BepInEx / .NET 运行时\n");
    if (preview.gameChanged) body.append("· 从当前游戏包提取元数据并重新生成代理\n");
    body.append("\n整个过程约 1–2 分钟，");
    body.append(preview.downloads() ? "建议在 Wi-Fi 下进行并保持游戏在前台。" : "请保持游戏在前台。");
    body.append("准备好之后的启动会快很多。");
    Diag.i(
        "game",
        "launch needs preparation · download=" + preview.downloads() + " bundled=" + preview.bundled + " gameChanged=" + preview.gameChanged);
    new Sheet(this, "启动游戏", "先准备运行环境")
        .message(body.toString())
        .primary("开始准备", this::beginLaunch)
        .secondary("取消", null)
        .show();
  }

  private boolean online() {
    ConnectivityManager manager = getSystemService(ConnectivityManager.class);
    Network network = manager.getActiveNetwork();
    if (network == null) return false;
    NetworkCapabilities capabilities = manager.getNetworkCapabilities(network);
    return capabilities != null
        && capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET);
  }

  private void beginLaunch() {
    if (launching) return;
    launching = true;
    launch.setEnabled(false);
    launch.label("准备中…");
    android.util.Log.i("BazaarHelper", "Preparing installed game");

    ProgressBar bar = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
    bar.setIndeterminate(true);
    bar.setIndeterminateTintList(android.content.res.ColorStateList.valueOf(Ui.ACCENT));
    bar.setLayoutParams(new LinearLayout.LayoutParams(-1, Ui.dp(8)));

    progressSheet =
        new Sheet(this, "启动游戏", "准备游戏环境")
            .message("正在校验运行时。游戏首次启动或更新后会生成代理，请保持在前台。")
            .body(bar)
            .cancelable(false);
    progressSheet.show();

    io.execute(
        () -> {
          try {
            Diag.i("game", "prepare start");
            engine().db.put("hookStatus", "正在初始化战绩采集…");
            RuntimeConfig config =
                RuntimeInstaller.prepare(
                    this,
                    message -> {
                      android.util.Log.i("BazaarHelper", message);
                      runOnUiThread(() -> progressSheet.message(message));
                    });
            runOnUiThread(
                () -> {
                  launching = false;
                  launch.setEnabled(true);
                  launch.label("启动游戏");
                  dismissProgress();
                  Intent i =
                      new Intent(this, GameHostActivity.class)
                          .addFlags(
                              Intent.FLAG_ACTIVITY_NEW_TASK
                                  | Intent.FLAG_ACTIVITY_REORDER_TO_FRONT
                                  | Intent.FLAG_ACTIVITY_SINGLE_TOP)
                          .putExtra(RuntimeConfig.EXTRA, config);
                  Diag.i("game", "launching");
                  startActivity(i);
                  refresh();
                });
          } catch (Exception e) {
            android.util.Log.e("BazaarHelper", "Prepare failed", e);
            Diag.e("game", "prepare failed", e);
            runOnUiThread(
                () -> {
                  launching = false;
                  launch.setEnabled(true);
                  launch.label("启动游戏");
                  dismissProgress();
                  error(
                      e instanceof PackageManager.NameNotFoundException
                          ? "请先安装《The Bazaar》Android 版。"
                          : "启动准备失败：" + e.getMessage());
                });
          }
        });
  }

  private void history() {
    io.execute(
        () -> {
          try {
            String owner = engine().db.session().optString("owner");
            List<JSONObject> records = engine().db.records(owner, null);
            if (records.isEmpty()) {
              runOnUiThread(() -> error("暂无本账号战绩。从助手启动游戏并完成一场 PvP 后会自动保存。"));
              return;
            }
            runOnUiThread(() -> showHistory(records));
          } catch (Exception e) {
            runOnUiThread(() -> error("战绩读取失败"));
          }
        });
  }

  private void showHistory(List<JSONObject> records) {
    TreeMap<Integer, int[]> days = new TreeMap<>(Comparator.reverseOrder());
    for (JSONObject record : records) {
      try {
        JSONObject battle = record.getJSONObject("detail").getJSONObject("battle");
        int[] tally = days.computeIfAbsent(battle.optInt("day"), day -> new int[2]);
        tally["win".equals(battle.optString("result")) ? 0 : 1]++;
      } catch (Exception ignored) {
      }
    }
    int totalWins = 0, totalLosses = 0;
    for (int[] tally : days.values()) {
      totalWins += tally[0];
      totalLosses += tally[1];
    }
    Sheet sheet =
        new Sheet(this, "战绩", days.size() + " 天 · " + records.size() + " 场 PvP")
            .message(records.size() + " 场里 " + totalWins + " 胜 " + totalLosses + " 负，每天一条汇总。");
    for (Map.Entry<Integer, int[]> day : days.entrySet()) {
      int wins = day.getValue()[0], losses = day.getValue()[1];
      LinearLayout row = Ui.row(this);
      row.setPadding(Ui.dp(4), Ui.dp(11), Ui.dp(4), Ui.dp(11));
      row.addView(Ui.pill(this, "第 " + day.getKey() + " 天", Ui.MUTED));
      LinearLayout tally = Ui.row(this);
      tally.setPadding(Ui.dp(12), 0, Ui.dp(8), 0);
      tally.addView(Ui.text(this, String.valueOf(wins), 15, Ui.WIN, Ui.title()));
      tally.addView(Ui.text(this, " 胜", 13, Ui.MUTED, Ui.strong()));
      tally.addView(Ui.text(this, "   ", 13, Ui.MUTED, Ui.strong()));
      tally.addView(Ui.text(this, String.valueOf(losses), 15, Ui.LOSS, Ui.title()));
      tally.addView(Ui.text(this, " 负", 13, Ui.MUTED, Ui.strong()));
      row.addView(tally, new LinearLayout.LayoutParams(0, -2, 1));
      row.addView(Ui.muted(this, "共 " + (wins + losses) + " 场"));
      sheet.item(row, null);
    }
    sheet.secondary("关闭", null).show();
  }

  private void syncNow() {
    if (syncing) return;
    syncing = true;
    syncButton.setEnabled(false);
    syncButton.label("同步中…");
    io.execute(
        () -> {
          String message;
          try {
            engine().sync();
            message = "同步完成";
            Diag.i("sync", "ok");
          } catch (Exception e) {
            message = describe(e);
            Diag.e("sync", "failed", e);
          }
          final String result = message;
          runOnUiThread(
              () -> {
                syncing = false;
                if (syncButton != null) {
                  syncButton.setEnabled(true);
                  syncButton.label("立即同步");
                }
                if (!isFinishing()) {
                  toast(result);
                  refresh();
                }
              });
        });
  }

  private void exportLogs() {
    if (exportButton == null) return;
    exportButton.setEnabled(false);
    exportButton.label("导出中…");
    io.execute(
        () -> {
          LogExport.Result result = null;
          String failure = null;
          try {
            result = LogExport.write(this);
          } catch (Exception e) {
            Diag.e("export", "failed", e);
            failure = "导出失败：" + describe(e);
          }
          final LogExport.Result ready = result;
          final String message = failure;
          runOnUiThread(
              () -> {
                if (exportButton != null) {
                  exportButton.setEnabled(true);
                  exportButton.label("导出日志");
                }
                if (ready == null) {
                  error(message == null ? "导出失败" : message);
                  return;
                }
                shareLog(ready);
              });
        });
  }

  private void shareLog(LogExport.Result result) {
    Intent send =
        new Intent(Intent.ACTION_SEND)
            .setType("application/zip")
            .putExtra(Intent.EXTRA_STREAM, result.uri)
            .putExtra(Intent.EXTRA_SUBJECT, result.name)
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
    try {
      startActivity(Intent.createChooser(send, "分享诊断日志"));
      toast("已导出 " + Math.max(1, result.bytes / 1024) + "KB 到 下载/BazaarHelper/" + result.name);
    } catch (Exception e) {
      error("已保存到 下载/BazaarHelper/" + result.name + "\n没有可用于分享的应用。");
    }
  }

  private void resetTimerPosition() {
    io.execute(
        () -> {
          try {
            BattleTimerOverlay.resetPosition(BattleTimerOverlay.defaultStore());
          } catch (Exception ignored) {
          }
        });
    toast("回到游戏后计时器回到默认位置");
  }

  private String describe(Exception e) {
    if (e instanceof HttpApi.HttpFailure)
      return "请求失败（HTTP " + ((HttpApi.HttpFailure) e).status + "），请稍后重试";
    String message = e.getMessage();
    return message == null || message.isEmpty() ? "操作未完成，请稍后重试" : message;
  }

  private void toast(String message) {
    Toast.makeText(this, message, Toast.LENGTH_SHORT).show();
  }

  private void error(String message) {
    if (isFinishing()) return;
    Diag.i("ui", "notice: " + message);
    new Sheet(this, null, "提示").message(message).primary("知道了", null).show();
  }
}
