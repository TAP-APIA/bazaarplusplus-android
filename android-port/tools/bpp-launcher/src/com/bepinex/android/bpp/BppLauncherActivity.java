package com.bepinex.android.bpp;

import android.app.Activity;
import android.app.ActivityManager;
import android.app.AlertDialog;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Bundle;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.Process;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;
import java.io.File;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Self-deploying BazaarPlusPlus Android launcher activity.
 *
 * Automatically extracts the BepInEx/.NET runtime and installs or updates the bundled
 * BPP modpack while preserving user configs and local history databases, then launches
 * The Bazaar through the verified BootstrapActivity pipeline.
 */
public final class BppLauncherActivity extends Activity {

    public static final String EXTRA_AUTO_LAUNCH = "bpp_auto_launch";

    private static final int COLOR_BG = Color.parseColor("#120E0A");
    private static final int COLOR_CARD = Color.parseColor("#1E1711");
    private static final int COLOR_CARD_SUBTLE = Color.parseColor("#18120D");
    private static final int COLOR_GOLD = Color.parseColor("#F5D176");
    private static final int COLOR_GOLD_BORDER = Color.parseColor("#8C6D32");
    private static final int COLOR_BUTTON_PRIMARY_BG = Color.parseColor("#3B2B15");
    private static final int COLOR_BUTTON_SECONDARY_BG = Color.parseColor("#241C14");
    private static final int COLOR_TEXT_PRIMARY = Color.parseColor("#F7EFE2");
    private static final int COLOR_TEXT_SECONDARY = Color.parseColor("#C9B99B");
    private static final int COLOR_STATUS_OK = Color.parseColor("#7CD992");
    private static final int COLOR_STATUS_WARN = Color.parseColor("#F2B866");
    private static final int COLOR_STATUS_ERR = Color.parseColor("#F27A66");

    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final ExecutorService updateWorker = Executors.newSingleThreadExecutor();
    private final ExecutorService diagnosticWorker = Executors.newSingleThreadExecutor();

    private TextView tvStatusBadge;
    private TextView tvStatusHeadline;
    private TextView tvStatusDetail;
    private TextView tvGameInfo;
    private TextView tvBundleInfo;
    private TextView tvPathInfo;
    private ProgressBar progressBar;
    private Button btnLaunch;
    private Button btnRepair;
    private Button btnAdvanced;
    private TextView tvUpdateInfo;
    private Button btnCheckUpdate;
    private Button btnDownloadUpdate;
    private Button btnExportDiagnostics;
    private TextView tvExportInfo;
    private TextView tvGenerationHint;
    private boolean generationWarningAccepted;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private volatile boolean exportingDiagnostics;
    private boolean checkingUpdate;
    private boolean updateChecked;
    private boolean resumed;
    private boolean updatePromptShown;
    private BppLauncherUpdateChecker.Update availableUpdate;
    private BppApkDownload apkDownload;
    private boolean validatingApk;
    private final Runnable downloadPoll = this::refreshApkDownload;

    private volatile boolean busy;
    private volatile boolean autoLaunchConsumed;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        try {
            android.util.Log.i("BppGame", BppBuildInfo.describe());
        } catch (Throwable t) {
            android.util.Log.w("BppGame", "BppBuildInfo.describe failed", t);
        }
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        // Target SDK 35 enforces edge-to-edge on newer Android versions. Keep the background
        // edge-to-edge, but inset the scrolling viewport (not its child) so scrolled text never
        // draws behind status/navigation bars or a rotated display cutout.
        getWindow().clearFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
        getWindow().setStatusBarColor(COLOR_BG);
        getWindow().setNavigationBarColor(COLOR_BG);
        if (Build.VERSION.SDK_INT >= 30) {
            getWindow().setDecorFitsSystemWindows(false);
        } else {
            getWindow().getDecorView().setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                    | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION);
        }
        initLauncherCore();
        try {
            BppDiagnosticSessionLog.ensureCapture(this, "launcher");
        } catch (Throwable ignored) {
        }
        apkDownload = new BppApkDownload(this);
        setContentView(buildContentView());
    }

    @Override
    protected void onResume() {
        super.onResume();
        resumed = true;
        if (apkDownload.alreadyInstalled(installedLauncherVersion())) apkDownload.clear();
        if (!updateChecked) checkLauncherUpdate();
        refreshApkDownload();
        showLauncherUpdatePrompt();
        clearPendingCrashBanner();
        refreshGenerationHint();
        boolean requestAutoLaunch = !autoLaunchConsumed
            && getIntent() != null
            && getIntent().getBooleanExtra(EXTRA_AUTO_LAUNCH, false);
        if (requestAutoLaunch) {
            autoLaunchConsumed = true;
        }
        startPrepareFlow(false, requestAutoLaunch, false);
    }

    @Override
    protected void onPause() {
        resumed = false;
        mainHandler.removeCallbacks(downloadPoll);
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        worker.shutdownNow();
        updateWorker.shutdownNow();
        diagnosticWorker.shutdownNow();
        mainHandler.removeCallbacksAndMessages(null);
    }

    private View buildContentView() {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(COLOR_BG);
        scroll.setOnApplyWindowInsetsListener((view, insets) -> {
            int left, top, right, bottom;
            if (Build.VERSION.SDK_INT >= 30) {
                // Read the controller only after the decor is attached; some vendor windows
                // dereference an uninitialized decor from Window.getInsetsController().
                WindowInsetsController controller = view.getWindowInsetsController();
                if (controller != null) {
                    controller.setSystemBarsAppearance(0,
                        WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS
                            | WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS);
                }
                android.graphics.Insets safe = insets.getInsets(
                    WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                left = safe.left;
                top = safe.top;
                right = safe.right;
                bottom = safe.bottom;
            } else {
                left = insets.getSystemWindowInsetLeft();
                top = insets.getSystemWindowInsetTop();
                right = insets.getSystemWindowInsetRight();
                bottom = insets.getSystemWindowInsetBottom();
                android.view.DisplayCutout cutout = insets.getDisplayCutout();
                if (cutout != null) {
                    left = Math.max(left, cutout.getSafeInsetLeft());
                    top = Math.max(top, cutout.getSafeInsetTop());
                    right = Math.max(right, cutout.getSafeInsetRight());
                    bottom = Math.max(bottom, cutout.getSafeInsetBottom());
                }
            }
            view.setPadding(left, top, right, bottom);
            return insets;
        });
        scroll.post(scroll::requestApplyInsets);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        int padH = dp(24);
        int padV = dp(20);
        root.setPadding(padH, padV, padH, padV);

        TextView kicker = new TextView(this);
        kicker.setText("BAZAARPLUSPLUS  ·  ANDROID 一体化启动器");
        kicker.setTextColor(COLOR_GOLD);
        kicker.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f);
        kicker.setTypeface(Typeface.DEFAULT_BOLD);
        root.addView(kicker);

        TextView title = new TextView(this);
        title.setText("The Bazaar 模组启动器");
        title.setTextColor(COLOR_TEXT_PRIMARY);
        title.setTextSize(TypedValue.COMPLEX_UNIT_SP, 24f);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        LinearLayout.LayoutParams titleLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        );
        titleLp.topMargin = dp(4);
        root.addView(title, titleLp);

        TextView subtitle = new TextView(this);
        subtitle.setText("内置 BPP 模组包与运行环境 · 自动部署更新 · 覆盖升级保留配置与历史记录");
        subtitle.setTextColor(COLOR_TEXT_SECONDARY);
        subtitle.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f);
        LinearLayout.LayoutParams subLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        );
        subLp.topMargin = dp(4);
        subLp.bottomMargin = dp(16);
        root.addView(subtitle, subLp);

        LinearLayout statusCard = createCard(COLOR_CARD, COLOR_GOLD_BORDER, dp(16));
        tvStatusBadge = new TextView(this);
        tvStatusBadge.setText("正在检查环境");
        tvStatusBadge.setTextColor(COLOR_GOLD);
        tvStatusBadge.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f);
        tvStatusBadge.setTypeface(Typeface.DEFAULT_BOLD);
        statusCard.addView(tvStatusBadge);

        tvStatusHeadline = new TextView(this);
        tvStatusHeadline.setContentDescription("bpp_status_title");
        tvStatusHeadline.setText("正在准备内置 BPP 模组…");
        tvStatusHeadline.setTextColor(COLOR_TEXT_PRIMARY);
        tvStatusHeadline.setTextSize(TypedValue.COMPLEX_UNIT_SP, 18f);
        tvStatusHeadline.setTypeface(Typeface.DEFAULT_BOLD);
        LinearLayout.LayoutParams headLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        );
        headLp.topMargin = dp(6);
        statusCard.addView(tvStatusHeadline, headLp);

        tvStatusDetail = new TextView(this);
        tvStatusDetail.setContentDescription("bpp_status_detail");
        tvStatusDetail.setText("首次打开或升级 APK 后会自动同步插件与修补核心，无需电脑 ADB 推送。");
        tvStatusDetail.setTextColor(COLOR_TEXT_SECONDARY);
        tvStatusDetail.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f);
        LinearLayout.LayoutParams detailLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        );
        detailLp.topMargin = dp(6);
        statusCard.addView(tvStatusDetail, detailLp);

        progressBar = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        progressBar.setMax(100);
        progressBar.setProgress(10);
        LinearLayout.LayoutParams progLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            dp(8)
        );
        progLp.topMargin = dp(12);
        statusCard.addView(progressBar, progLp);

        root.addView(statusCard, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        ));

        btnLaunch = createActionButton(
            "启动 The Bazaar（BPP 模组）",
            "bpp_btn_launch",
            COLOR_BUTTON_PRIMARY_BG,
            COLOR_GOLD,
            COLOR_GOLD,
            16f,
            true
        );
        btnLaunch.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                if (generationWarningAccepted || !shouldWarnBeforeLaunch()) {
                    startPrepareFlow(false, true, false);
                    return;
                }
                new AlertDialog.Builder(BppLauncherActivity.this)
                    .setTitle("请留在游戏画面")
                    .setMessage(launchWarningMessage())
                    .setPositiveButton("知道了，开始", (dialog, which) -> {
                        generationWarningAccepted = true;
                        startPrepareFlow(false, true, false);
                    })
                    .setNegativeButton("取消", null)
                    .show();
            }
        });
        LinearLayout.LayoutParams launchLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            dp(54)
        );
        launchLp.topMargin = dp(16);
        root.addView(btnLaunch, launchLp);

        tvGenerationHint = new TextView(this);
        tvGenerationHint.setContentDescription("bpp_generation_hint");
        tvGenerationHint.setTextColor(COLOR_GOLD);
        tvGenerationHint.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f);
        tvGenerationHint.setVisibility(View.GONE);
        LinearLayout.LayoutParams hintLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        );
        hintLp.topMargin = dp(8);
        root.addView(tvGenerationHint, hintLp);

        LinearLayout secondaryRow = new LinearLayout(this);
        secondaryRow.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams rowLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        );
        rowLp.topMargin = dp(10);

        btnRepair = createActionButton(
            "重新校验 / 修复模组",
            "bpp_btn_repair",
            COLOR_BUTTON_SECONDARY_BG,
            COLOR_GOLD_BORDER,
            COLOR_TEXT_PRIMARY,
            13f,
            false
        );
        btnRepair.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                startPrepareFlow(true, false, true);
            }
        });
        LinearLayout.LayoutParams leftBtnLp = new LinearLayout.LayoutParams(0, dp(46), 1f);
        leftBtnLp.rightMargin = dp(6);
        secondaryRow.addView(btnRepair, leftBtnLp);

        btnAdvanced = createActionButton(
            "打开原版高级启动器",
            "bpp_btn_advanced",
            COLOR_BUTTON_SECONDARY_BG,
            COLOR_GOLD_BORDER,
            COLOR_TEXT_SECONDARY,
            13f,
            false
        );
        btnAdvanced.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                openOriginalLauncher();
            }
        });
        LinearLayout.LayoutParams rightBtnLp = new LinearLayout.LayoutParams(0, dp(46), 1f);
        rightBtnLp.leftMargin = dp(6);
        secondaryRow.addView(btnAdvanced, rightBtnLp);

        root.addView(secondaryRow, rowLp);

        btnExportDiagnostics = createActionButton(
            "导出诊断日志",
            "bpp_btn_export_diagnostics",
            COLOR_BUTTON_SECONDARY_BG,
            COLOR_GOLD,
            COLOR_GOLD,
            15f,
            true
        );
        btnExportDiagnostics.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                exportDiagnostics();
            }
        });
        LinearLayout.LayoutParams exportLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            dp(48)
        );
        exportLp.topMargin = dp(10);
        root.addView(btnExportDiagnostics, exportLp);

        TextView exportHint = new TextView(this);
        exportHint.setText("灰屏或闪退后回到本页再点。会保存到「下载」并打开分享，包含设备信息与启动日志，不含对局数据库和签名私钥。");
        exportHint.setTextColor(COLOR_TEXT_SECONDARY);
        exportHint.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f);
        LinearLayout.LayoutParams exportHintLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        );
        exportHintLp.topMargin = dp(6);
        root.addView(exportHint, exportHintLp);

        tvExportInfo = new TextView(this);
        tvExportInfo.setContentDescription("bpp_export_path");
        tvExportInfo.setTextColor(COLOR_GOLD);
        tvExportInfo.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f);
        tvExportInfo.setVisibility(View.GONE);
        LinearLayout.LayoutParams exportInfoLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        );
        exportInfoLp.topMargin = dp(4);
        root.addView(tvExportInfo, exportInfoLp);

        LinearLayout infoCard = createCard(COLOR_CARD_SUBTLE, COLOR_GOLD_BORDER, dp(14));
        LinearLayout.LayoutParams infoCardLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        );
        infoCardLp.topMargin = dp(14);

        tvGameInfo = createInfoLine("游戏状态：正在检测…");
        tvBundleInfo = createInfoLine("内置模组：正在读取清单…");
        tvPathInfo = createInfoLine("数据保护：保留 BazaarPlusPlus.cfg 与 BazaarPlusPlusV6 本地历史");
        infoCard.addView(tvGameInfo);
        infoCard.addView(tvBundleInfo);
        infoCard.addView(tvPathInfo);
        root.addView(infoCard, infoCardLp);

        LinearLayout updateCard = createCard(COLOR_CARD_SUBTLE, COLOR_GOLD_BORDER, dp(14));
        tvUpdateInfo = createInfoLine("启动器版本：" + installedLauncherVersion());
        tvUpdateInfo.setContentDescription("bpp_update_status");
        updateCard.addView(tvUpdateInfo);
        btnCheckUpdate = createActionButton("检查启动器更新", "bpp_btn_check_update",
            COLOR_BUTTON_SECONDARY_BG, COLOR_GOLD_BORDER, COLOR_TEXT_PRIMARY, 13f, false);
        btnCheckUpdate.setOnClickListener(v -> checkLauncherUpdate());
        updateCard.addView(btnCheckUpdate);
        btnDownloadUpdate = createActionButton("下载新版 APK", "bpp_btn_download_update",
            COLOR_BUTTON_PRIMARY_BG, COLOR_GOLD_BORDER, COLOR_GOLD, 13f, true);
        btnDownloadUpdate.setVisibility(View.GONE);
        btnDownloadUpdate.setOnClickListener(v -> downloadOrInstallUpdate());
        updateCard.addView(btnDownloadUpdate);
        Button releasePage = createActionButton("GitHub 下载页（备用）", "bpp_btn_release_page",
            COLOR_BUTTON_SECONDARY_BG, COLOR_GOLD_BORDER, COLOR_TEXT_SECONDARY, 13f, false);
        releasePage.setOnClickListener(v -> openReleasePage());
        updateCard.addView(releasePage);
        TextView updateHint = createInfoLine("仅检查本项目启动器（含预览版）；不会自动替换上游资源。覆盖安装，请勿卸载或清除数据。");
        updateCard.addView(updateHint);
        LinearLayout.LayoutParams updateLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        updateLp.topMargin = dp(14);
        root.addView(updateCard, updateLp);

        scroll.addView(root);
        return scroll;
    }

    private String installedLauncherVersion() {
        try {
            return getPackageManager().getPackageInfo(getPackageName(), 0).versionName;
        } catch (Exception ex) {
            return "未知";
        }
    }

    private void checkLauncherUpdate() {
        if (checkingUpdate || isDestroyed()) return;
        checkingUpdate = true;
        updateChecked = true;
        btnCheckUpdate.setEnabled(false);
        final String currentVersion = installedLauncherVersion();
        tvUpdateInfo.setText("启动器 " + currentVersion + " · 正在检查 GitHub 更新…");
        updateWorker.execute(() -> {
            BppLauncherUpdateChecker.Update found = null;
            String error = null;
            try {
                found = BppLauncherUpdateChecker.check(currentVersion);
            } catch (Exception ex) {
                error = "检查失败，请检查网络或稍后重试";
                logError("Launcher update check failed", ex);
            }
            final BppLauncherUpdateChecker.Update result = found;
            final String failure = error;
            runOnUiThread(() -> {
                if (isDestroyed() || isFinishing()) return;
                checkingUpdate = false;
                btnCheckUpdate.setEnabled(true);
                availableUpdate = result;
                btnDownloadUpdate.setVisibility(result == null ? View.GONE : View.VISIBLE);
                tvUpdateInfo.setText("启动器 " + currentVersion + " · " + (failure != null ? failure
                    : result == null ? "未发现更新版本（含预览版）" : "发现新版 " + result.version));
                showLauncherUpdatePrompt();
                refreshApkDownload();
            });
        });
    }

    private void showLauncherUpdatePrompt() {
        if (!resumed || isFinishing() || isDestroyed() || availableUpdate == null || updatePromptShown || apkDownload.hasDownload()) return;
        updatePromptShown = true;
        new AlertDialog.Builder(this)
            .setTitle("发现启动器新版 " + availableUpdate.version)
            .setMessage("当前版本：" + installedLauncherVersion()
                + "\n在 App 内下载 APK，完成后点击安装并按系统提示覆盖升级。请勿卸载或清除数据，以保留配置与历史记录。")
            .setPositiveButton("下载新版", (dialog, which) -> downloadOrInstallUpdate())
            .setNegativeButton("稍后", null)
            .show();
    }

    private void refreshApkDownload() {
        mainHandler.removeCallbacks(downloadPoll);
        if (!resumed || isDestroyed() || validatingApk || !apkDownload.hasDownload()) return;
        try {
            BppApkDownload.State state = apkDownload.state();
            tvUpdateInfo.setText(state.message);
            btnDownloadUpdate.setVisibility(View.VISIBLE);
            btnDownloadUpdate.setText(state.complete ? "安装已下载的新版" : "正在下载…");
            btnDownloadUpdate.setEnabled(state.complete);
            if (!state.complete) mainHandler.postDelayed(downloadPoll, 1000);
        } catch (Exception error) {
            apkDownload.clear();
            btnDownloadUpdate.setEnabled(true);
            btnDownloadUpdate.setText("重新下载新版 APK");
            btnDownloadUpdate.setVisibility(availableUpdate == null ? View.GONE : View.VISIBLE);
            tvUpdateInfo.setText(error.getMessage());
        }
    }

    private void downloadOrInstallUpdate() {
        try {
            if (!apkDownload.hasDownload()) {
                if (availableUpdate == null) return;
                apkDownload.start(availableUpdate);
                refreshApkDownload();
                return;
            }
            if (!apkDownload.state().complete) return;
            if (validatingApk) return;
            validatingApk = true;
            mainHandler.removeCallbacks(downloadPoll);
            btnDownloadUpdate.setEnabled(false);
            tvUpdateInfo.setText("正在校验安装包…");
            updateWorker.execute(() -> {
                Uri uri = null;
                Exception failure = null;
                try { uri = apkDownload.installationUri(); }
                catch (Exception error) { failure = error; }
                final Uri result = uri;
                final Exception error = failure;
                runOnUiThread(() -> {
                    validatingApk = false;
                    if (isDestroyed() || isFinishing()) return;
                    refreshApkDownload();
                    // Returning from the background leaves an explicit install button.
                    if (!resumed) return;
                    if (error != null) showApkUpdateError(error);
                    else launchApkInstaller(result);
                });
            });
        } catch (Exception error) {
            showApkUpdateError(error);
        }
    }

    private void launchApkInstaller(Uri apk) {
        try {
            if (!getPackageManager().canRequestPackageInstalls()) {
                new AlertDialog.Builder(this)
                    .setTitle("允许安装更新")
                    .setMessage("请在系统设置中允许 BPP 安装应用，返回后再次点击「安装已下载的新版」。")
                    .setPositiveButton("打开设置", (dialog, which) -> {
                        try {
                            startActivity(new Intent(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                                Uri.parse("package:" + getPackageName())));
                        } catch (Exception error) {
                            Toast.makeText(this, "无法打开设置，请在系统设置中允许安装未知应用", Toast.LENGTH_LONG).show();
                        }
                    }).setNegativeButton("稍后", null).show();
                return;
            }
            Intent install = new Intent(Intent.ACTION_VIEW)
                .setDataAndType(apk, "application/vnd.android.package-archive")
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            install.setClipData(ClipData.newRawUri("BPP update", apk));
            startActivity(install);
        } catch (Exception error) {
            showApkUpdateError(error);
        }
    }

    private void showApkUpdateError(Exception error) {
        logError("Launcher APK update failed", error);
        new AlertDialog.Builder(this)
            .setTitle("更新未完成")
            .setMessage(error.getMessage() + "\n可重新下载，或打开 GitHub 下载页。请勿卸载应用。")
            .setPositiveButton("重新下载", (dialog, which) -> {
                apkDownload.clear();
                btnDownloadUpdate.setText("下载新版 APK");
                btnDownloadUpdate.setEnabled(true);
                if (availableUpdate != null) downloadOrInstallUpdate();
                else checkLauncherUpdate();
            })
            .setNeutralButton("GitHub 下载页", (dialog, which) -> openReleasePage()).setNegativeButton("稍后", null).show();
    }

    private void openReleasePage() {
        try {
            startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(availableUpdate == null
                ? BppLauncherUpdateChecker.RELEASES_URL : availableUpdate.releaseUrl)));
        } catch (Exception ignored) {
            Toast.makeText(this, "无法打开浏览器", Toast.LENGTH_LONG).show();
        }
    }

    private TextView createInfoLine(String text) {
        TextView tv = new TextView(this);
        tv.setText(text);
        tv.setTextColor(COLOR_TEXT_SECONDARY);
        tv.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        );
        lp.topMargin = dp(4);
        tv.setLayoutParams(lp);
        return tv;
    }

    private LinearLayout createCard(int bgColor, int strokeColor, int paddingPx) {
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        layout.setPadding(paddingPx, paddingPx, paddingPx, paddingPx);
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(bgColor);
        bg.setCornerRadius(dp(12));
        bg.setStroke(dp(1), strokeColor);
        layout.setBackground(bg);
        return layout;
    }

    private Button createActionButton(
        String label,
        String contentDesc,
        int bgColor,
        int strokeColor,
        int textColor,
        float textSp,
        boolean bold
    ) {
        Button btn = new Button(this);
        btn.setAllCaps(false);
        btn.setText(label);
        btn.setContentDescription(contentDesc);
        btn.setTextColor(textColor);
        btn.setTextSize(TypedValue.COMPLEX_UNIT_SP, textSp);
        btn.setTypeface(bold ? Typeface.DEFAULT_BOLD : Typeface.DEFAULT);
        btn.setGravity(Gravity.CENTER);
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(bgColor);
        bg.setCornerRadius(dp(10));
        bg.setStroke(dp(1), strokeColor);
        btn.setBackground(bg);
        return btn;
    }

    private void startPrepareFlow(
        final boolean forceRepair,
        final boolean launchWhenReady,
        final boolean stopRunningGameIfNeeded
    ) {
        if (busy) {
            return;
        }
        busy = true;
        setButtonsEnabled(false);
        worker.execute(new Runnable() {
            @Override
            public void run() {
                try {
                    runPrepareFlowOnWorker(forceRepair, launchWhenReady, stopRunningGameIfNeeded);
                } finally {
                    busy = false;
                    runOnUiThread(new Runnable() {
                        @Override
                        public void run() {
                            setButtonsEnabled(true);
                        }
                    });
                }
            }
        });
    }

    private void runPrepareFlowOnWorker(
        boolean forceRepair,
        boolean launchWhenReady,
        boolean stopRunningGameIfNeeded
    ) {
        final String pkg = BppBundledModpackInstaller.TARGET_PACKAGE;
        final PackageInfo gamePkg = findInstalledGamePackage(pkg);
        final File gameRootDir = resolveGameRootDir(pkg);
        final File internalWorkDir = new File(getFilesDir(), "bpp-bundled-installer");

        BppBundledModpackInstaller.Manifest manifest;
        try {
            manifest = BppBundledModpackInstaller.parseManifest(
                getAssets().open(BppBundledModpackInstaller.ASSET_MODPACK_MANIFEST)
            );
        } catch (final Exception ex) {
            logError("Failed to read bundled manifest", ex);
            notePrepare("manifest-error", String.valueOf(ex.getMessage()));
            updateUiState(
                "清单错误",
                COLOR_STATUS_ERR,
                "无法读取内置模组清单",
                String.valueOf(ex.getMessage()),
                100,
                gamePkg,
                null,
                gameRootDir
            );
            return;
        }

        final BppBundledModpackInstaller.Manifest resolvedManifest = manifest;
        BppBundledModpackInstaller.StatusReport current = BppBundledModpackInstaller.inspectStatus(
            gameRootDir,
            internalWorkDir,
            resolvedManifest,
            true
        );

        int gamePid = findRunningGameProcessPid();
        if (gamePid > 0 && (!current.isReady() || forceRepair)) {
            if (stopRunningGameIfNeeded) {
                Process.killProcess(gamePid);
                try {
                    Thread.sleep(600L);
                } catch (InterruptedException ignored) {
                    Thread.currentThread().interrupt();
                }
            } else {
                updateUiState(
                    "游戏运行中",
                    COLOR_STATUS_WARN,
                    "检测到游戏进程正在运行，暂不写入模组文件",
                    "请先关闭游戏，或点击「重新校验 / 修复模组」自动停止游戏进程并完成更新。",
                    100,
                    gamePkg,
                    resolvedManifest,
                    gameRootDir
                );
                notePrepare("game-running", "pid=" + gamePid);
                return;
            }
        }

        try {
            updateUiState(
                "正在准备",
                COLOR_GOLD,
                "正在检查 BepInEx 与 .NET 运行环境…",
                "首次安装或更新时会自动解包运行环境与内置模组。",
                20,
                gamePkg,
                resolvedManifest,
                gameRootDir
            );
            ensureFrameworkExtracted(pkg);

            BppBundledModpackInstaller.StatusReport finalReport = BppBundledModpackInstaller.installOrUpdate(
                gameRootDir,
                internalWorkDir,
                new BppBundledModpackInstaller.StreamSupplier() {
                    @Override
                    public java.io.InputStream open() throws java.io.IOException {
                        return getAssets().open(BppBundledModpackInstaller.ASSET_MODPACK_MANIFEST);
                    }
                },
                new BppBundledModpackInstaller.StreamSupplier() {
                    @Override
                    public java.io.InputStream open() throws java.io.IOException {
                        return getAssets().open(BppBundledModpackInstaller.ASSET_MODPACK_ZIP);
                    }
                },
                new BppBundledModpackInstaller.StreamSupplier() {
                    @Override
                    public java.io.InputStream open() throws java.io.IOException {
                        return getAssets().open(BppBundledModpackInstaller.ASSET_BEPINEX_ZIP);
                    }
                },
                forceRepair,
                new BppBundledModpackInstaller.ProgressListener() {
                    @Override
                    public void onProgress(String step, int percent) {
                        updateUiState(
                            "正在同步",
                            COLOR_GOLD,
                            step,
                            "内置版本 " + resolvedManifest.version + " · 保留现有配置与历史记录",
                            percent,
                            gamePkg,
                            resolvedManifest,
                            gameRootDir
                        );
                    }
                }
            );

            if (gamePkg == null) {
                updateUiState(
                    "模组已就绪 · 待安装游戏",
                    COLOR_STATUS_WARN,
                    "内置 BPP 模组已部署完成，但尚未检测到 The Bazaar 本体",
                    "请先在设备安装 The Bazaar (" + pkg + ")，随后返回本应用点击启动。",
                    100,
                    null,
                    resolvedManifest,
                    gameRootDir
                );
                notePrepare("mod-ready-no-game", pkg);
                return;
            }

            updateUiState(
                "已就绪",
                COLOR_STATUS_OK,
                finalReport.detail + " · 可直接启动游戏",
                "点击下方「启动 The Bazaar（BPP 模组）」即可进入游戏；触屏入口支持长按拖动。",
                100,
                gamePkg,
                resolvedManifest,
                gameRootDir
            );

            notePrepare("ready", finalReport.detail);
            if (launchWhenReady) {
                launchBazaarWithBpp(pkg);
            }
        } catch (final Exception ex) {
            logError("BPP bundled modpack preparation failed", ex);
            notePrepare("failed", String.valueOf(ex.getMessage()));
            updateUiState(
                "需要修复",
                COLOR_STATUS_ERR,
                "内置模组部署未完成",
                String.valueOf(ex.getMessage()),
                100,
                gamePkg,
                resolvedManifest,
                gameRootDir
            );
        }
    }

    private void launchBazaarWithBpp(final String packageName) {
        try {
            if (!isFrameworkReady(packageName)) {
                ensureFrameworkExtracted(packageName);
            }
            boolean useOriginalLibUnity = !readAppSettingBoolean(
                "isUseUnstrippedLibUnityFlow",
                packageName,
                false
            );
            boolean blockUnityKill = readAppSettingBoolean(
                "isUnityKillBlockEnabledFlow",
                packageName,
                false
            );
            boolean screenDim = readAppSettingBoolean(
                "getScreenDimInGameEnabledFlow",
                null,
                false
            );
            boolean floatingLog = readAppSettingBoolean(
                "getFloatingLogInGameEnabledFlow",
                null,
                false
            );
            recordActiveModpackSelection(packageName, BppBundledModpackInstaller.MODPACK_NAME);
            markCrashDiagnosticsLaunch(packageName);
            patchGameLoggingConfig(packageName);
            BppDiagnosticSessionLog.noteLaunch(
                this,
                BppDiagnosticBundle.nowIso()
                    + " event=launch"
                    + " target=" + packageName
                    + " pid=" + Process.myPid()
                    + " gamePid=pending"
                    + " ogLibUnity=" + useOriginalLibUnity
                    + " blockUnityKill=" + blockUnityKill
                    + " screenDim=" + screenDim
                    + " floatingLog=" + floatingLog
                    + " modpack=" + BppBundledModpackInstaller.MODPACK_NAME
            );
            scheduleGamePidNotes();

            final Class<?> bootstrapClass = Class.forName("com.bepinex.android.BootstrapActivity");
            runOnUiThread(new Runnable() {
                @Override
                public void run() {
                    try {
                        Intent intent = new Intent(BppLauncherActivity.this, bootstrapClass);
                        intent.putExtra("target_package", packageName);
                        intent.putExtra("og_libunity", useOriginalLibUnity);
                        intent.putExtra("block_unity_kill", blockUnityKill);
                        intent.putExtra("screen_dim", screenDim);
                        intent.putExtra("floating_log", floatingLog);
                        ArrayList<String> modpacks = new ArrayList<String>();
                        modpacks.add(BppBundledModpackInstaller.MODPACK_NAME);
                        intent.putStringArrayListExtra("active_modpacks", modpacks);
                        intent.putExtra("active_modpack", BppBundledModpackInstaller.MODPACK_NAME);
                        startActivity(intent);
                    } catch (Exception ex) {
                        logError("Failed to start BootstrapActivity", ex);
                        Toast.makeText(
                            BppLauncherActivity.this,
                            "启动失败: " + ex.getMessage(),
                            Toast.LENGTH_LONG
                        ).show();
                    }
                }
            });
        } catch (final Exception ex) {
            logError("Failed preparing game launch", ex);
            runOnUiThread(new Runnable() {
                @Override
                public void run() {
                    Toast.makeText(
                        BppLauncherActivity.this,
                        "启动准备失败: " + ex.getMessage(),
                        Toast.LENGTH_LONG
                    ).show();
                }
            });
        }
    }

    private void openOriginalLauncher() {
        try {
            Class<?> mainClass = Class.forName("com.bepinex.android.MainActivity");
            Intent intent = new Intent(this, mainClass);
            startActivity(intent);
        } catch (Exception ex) {
            Toast.makeText(this, "无法打开高级界面: " + ex.getMessage(), Toast.LENGTH_LONG).show();
        }
    }

    private void updateUiState(
        final String badge,
        final int badgeColor,
        final String headline,
        final String detail,
        final int progress,
        final PackageInfo gamePkg,
        final BppBundledModpackInstaller.Manifest manifest,
        final File gameRootDir
    ) {
        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                tvStatusBadge.setText(badge);
                tvStatusBadge.setTextColor(badgeColor);
                tvStatusHeadline.setText(headline);
                tvStatusDetail.setText(detail);
                progressBar.setProgress(Math.max(0, Math.min(100, progress)));
                if (gamePkg != null) {
                    tvGameInfo.setText(
                        "游戏状态：已安装 The Bazaar · 版本 "
                            + (gamePkg.versionName != null ? gamePkg.versionName : "未知")
                    );
                } else {
                    tvGameInfo.setText("游戏状态：未检测到 com.tempogames.thebazaar（请先安装游戏本体）");
                }
                if (manifest != null) {
                    String shortHash = manifest.bundleId.length() >= 12
                        ? manifest.bundleId.substring(0, 12)
                        : manifest.bundleId;
                    tvBundleInfo.setText(
                        "内置模组：BPP " + manifest.version
                            + " · " + manifest.managedDllCount + " 个托管程序集 · SHA256 " + shortHash
                    );
                }
                if (gameRootDir != null) {
                    tvPathInfo.setText("运行目录：" + gameRootDir.getAbsolutePath());
                }
            }
        });
    }

    private void setButtonsEnabled(boolean enabled) {
        if (btnLaunch != null) {
            btnLaunch.setEnabled(enabled);
            btnLaunch.setAlpha(enabled ? 1.0f : 0.55f);
        }
        if (btnRepair != null) {
            btnRepair.setEnabled(enabled);
            btnRepair.setAlpha(enabled ? 1.0f : 0.55f);
        }
    }

    private PackageInfo findInstalledGamePackage(String packageName) {
        try {
            PackageManager pm = getPackageManager();
            if (pm.getLaunchIntentForPackage(packageName) == null) {
                return null;
            }
            return pm.getPackageInfo(packageName, 0);
        } catch (Exception ex) {
            return null;
        }
    }

    private boolean shouldWarnBeforeLaunch() {
        return interopDllCount() <= 0 || killedDuringGeneration();
    }

    private String launchWarningMessage() {
        if (killedDuringGeneration()) {
            return "上次生成模组文件时游戏被系统杀掉了（内存不足）。当时游戏不在前台，系统会优先回收它。"
                + "请重新启动后留在灰色画面，上面会显示「模组准备中」。不要切回启动器或桌面，也不要划掉通知，直到进入游戏。";
        }
        return BppDiagnosticBundle.FIRST_RUN_OVERLAY_TEXT
            + "。进入游戏后灰色画面上会显示「模组准备中」。请一直留在游戏里，不要划掉通知。";
    }

    private void refreshGenerationHint() {
        if (tvGenerationHint == null) return;
        try {
            if (killedDuringGeneration()) {
                tvGenerationHint.setVisibility(View.VISIBLE);
                tvGenerationHint.setText("上次生成模组文件时，游戏因为切到后台被系统杀掉了（内存不足）。"
                    + "请再启动一次，留在灰色画面上的「模组准备中」直到进入游戏，不要划掉通知。");
                return;
            }
            if (interopDllCount() <= 0) {
                tvGenerationHint.setVisibility(View.VISIBLE);
                tvGenerationHint.setText(BppDiagnosticBundle.FIRST_RUN_OVERLAY_TEXT
                    + "。灰色画面上会显示进度，请不要切走，也不要划掉通知。");
                return;
            }
            tvGenerationHint.setVisibility(View.GONE);
        } catch (Throwable ignored) {
            tvGenerationHint.setVisibility(View.GONE);
        }
    }

    private boolean killedDuringGeneration() {
        String state = readGenerationState();
        if (state.indexOf("stage=ready") >= 0) return false;
        boolean inProgress = state.indexOf("stage=cpp2il") >= 0
            || state.indexOf("stage=interop") >= 0
            || state.indexOf("stage=unity-libs") >= 0
            || state.indexOf("stage=starting") >= 0
            || state.indexOf("stage=plugins") >= 0;
        if (!inProgress) return false;
        String exit = BppDiagnosticExporter.latestGameExitNote(this);
        return exit.indexOf("reasonName=LOW_MEMORY") >= 0 || exit.indexOf("reason=3") >= 0;
    }

    private String readGenerationState() {
        try {
            File file = new File(BppDiagnosticSessionLog.diagnosticsDir(this), "generation-state.txt");
            if (!file.isFile() || file.length() > 8192) return "";
            byte[] data = java.nio.file.Files.readAllBytes(file.toPath());
            return new String(data, java.nio.charset.StandardCharsets.UTF_8);
        } catch (Exception ex) {
            return "";
        }
    }

    private int interopDllCount() {
        BppDiagnosticBundle.DirStat stat = BppDiagnosticBundle.summarizeTree(
            new File(resolveGameRootDir(BppBundledModpackInstaller.TARGET_PACKAGE), "BepInEx/interop"),
            ".dll",
            2
        );
        return stat.files;
    }

    private void patchGameLoggingConfig(String packageName) {
        File root = resolveGameRootDir(packageName);
        patchCfg(new File(root, "BepInEx/config/BepInEx.cfg"));
        patchCfg(new File(root, "modpacks/bpp/config/BepInEx.cfg"));
    }

    private void patchCfg(File cfg) {
        try {
            if (cfg == null || !cfg.isFile()) return;
            byte[] raw = java.nio.file.Files.readAllBytes(cfg.toPath());
            if (raw.length > 1024 * 1024) return;
            String text = new String(raw, java.nio.charset.StandardCharsets.UTF_8);
            String updated = BppDiagnosticBundle.enableDiagnosticLogging(text);
            if (updated.equals(text)) return;
            File tmp = new File(cfg.getAbsolutePath() + ".tmp");
            java.nio.file.Files.write(tmp.toPath(), updated.getBytes(java.nio.charset.StandardCharsets.UTF_8));
            if (!tmp.renameTo(cfg)) {
                java.nio.file.Files.write(cfg.toPath(), updated.getBytes(java.nio.charset.StandardCharsets.UTF_8));
                tmp.delete();
            }
            BppDiagnosticSessionLog.note(this, "prepare-results.log",
                BppDiagnosticBundle.nowIso() + " outcome=logging-config detail=" + cfg.getName());
        } catch (Exception ignored) {
        }
    }

    private void scheduleGamePidNotes() {
        final int[] delays = new int[] {1000, 2000, 4000, 8000, 15000, 30000, 60000};
        for (int i = 0; i < delays.length; i++) {
            final int delay = delays[i];
            mainHandler.postDelayed(new Runnable() {
                @Override
                public void run() {
                    int gamePid = findRunningGameProcessPid();
                    BppDiagnosticSessionLog.note(
                        BppLauncherActivity.this,
                        "launch-attempts.log",
                        BppDiagnosticBundle.nowIso()
                            + " event=game-pid gamePid=" + gamePid
                            + " afterMs=" + delay
                    );
                }
            }, delay);
        }
    }

    private int findRunningGameProcessPid() {
        try {
            ActivityManager am = (ActivityManager) getSystemService(Context.ACTIVITY_SERVICE);
            if (am == null) {
                return -1;
            }
            List<ActivityManager.RunningAppProcessInfo> procs = am.getRunningAppProcesses();
            if (procs == null) {
                return -1;
            }
            String expected = getPackageName() + ":game";
            for (ActivityManager.RunningAppProcessInfo info : procs) {
                if (info != null && expected.equals(info.processName) && info.pid > 0) {
                    return info.pid;
                }
            }
        } catch (Exception ignored) {
        }
        return -1;
    }

    private void initLauncherCore() {
        try {
            Class<?> pathsClass = Class.forName("com.bepinex.android.BepInExPaths");
            Object pathsInstance = pathsClass.getField("INSTANCE").get(null);
            pathsClass.getMethod("install", Context.class).invoke(pathsInstance, this);
        } catch (Exception ignored) {
        }
        try {
            Class<?> settingsClass = Class.forName("com.bepinex.android.settings.AppSettings");
            Object settingsInstance = settingsClass.getField("INSTANCE").get(null);
            settingsClass.getMethod("initialize", Context.class).invoke(settingsInstance, this);
        } catch (Exception ignored) {
        }
        try {
            Class<?> logClass = Class.forName("com.bepinex.android.BepInExLog");
            Object logInstance = logClass.getField("INSTANCE").get(null);
            logClass.getMethod("init", Context.class).invoke(logInstance, this);
            logClass.getMethod("i", String.class).invoke(logInstance, "=== BPP Integrated Launcher ===");
        } catch (Exception ignored) {
        }
    }

    private void clearPendingCrashBanner() {
        try {
            Class<?> crashClass = Class.forName("com.bepinex.android.CrashDiagnostics");
            Object crashInstance = crashClass.getField("INSTANCE").get(null);
            crashClass.getMethod("clearPending", Context.class).invoke(crashInstance, this);
        } catch (Exception ignored) {
        }
    }

    private void markCrashDiagnosticsLaunch(String packageName) {
        try {
            Class<?> crashClass = Class.forName("com.bepinex.android.CrashDiagnostics");
            Object crashInstance = crashClass.getField("INSTANCE").get(null);
            crashClass.getMethod("markLaunch", Context.class, String.class)
                .invoke(crashInstance, this, packageName);
        } catch (Exception ignored) {
        }
    }

    private File resolveGameRootDir(String packageName) {
        try {
            Class<?> pathsClass = Class.forName("com.bepinex.android.BepInExPaths");
            Object pathsInstance = pathsClass.getField("INSTANCE").get(null);
            Object dir = pathsClass.getMethod("getGameRootDir", String.class)
                .invoke(pathsInstance, packageName);
            if (dir instanceof File) {
                return (File) dir;
            }
        } catch (Exception ignored) {
        }
        File external = getExternalFilesDir(null);
        if (external == null) {
            external = getFilesDir();
        }
        return new File(external, "BepInEx_Android/" + packageName);
    }

    private boolean isFrameworkReady(String packageName) {
        try {
            Class<?> extractorClass = Class.forName("com.bepinex.android.FileExtractor");
            Object extractor = extractorClass.getConstructor(Context.class).newInstance(this);
            Object ready = extractorClass.getMethod("isFrameworkReady", String.class)
                .invoke(extractor, packageName);
            return Boolean.TRUE.equals(ready);
        } catch (Exception ex) {
            return false;
        }
    }

    private void ensureFrameworkExtracted(String packageName) throws Exception {
        Class<?> extractorClass = Class.forName("com.bepinex.android.FileExtractor");
        Object extractor = extractorClass.getConstructor(Context.class).newInstance(this);
        Class<?> fn1Class = Class.forName("kotlin.jvm.functions.Function1");
        Method extractBep = extractorClass.getMethod(
            "extractBepInExIfNeeded$default",
            extractorClass,
            String.class,
            fn1Class,
            int.class,
            Object.class
        );
        extractBep.invoke(null, extractor, packageName, null, 2, null);

        Method extractDotnet = extractorClass.getMethod(
            "extractDotnetIfNeeded$default",
            extractorClass,
            String.class,
            fn1Class,
            int.class,
            Object.class
        );
        extractDotnet.invoke(null, extractor, packageName, null, 2, null);
    }

    private boolean readAppSettingBoolean(String flowMethodName, String packageName, boolean fallback) {
        try {
            Class<?> settingsClass = Class.forName("com.bepinex.android.settings.AppSettings");
            final Object settingsInstance = settingsClass.getField("INSTANCE").get(null);
            final Object flow;
            if (packageName != null) {
                flow = settingsClass.getMethod(flowMethodName, Context.class, String.class)
                    .invoke(settingsInstance, this, packageName);
            } else {
                flow = settingsClass.getMethod(flowMethodName, Context.class)
                    .invoke(settingsInstance, this);
            }
            Class<?> flowClass = Class.forName("kotlinx.coroutines.flow.Flow");
            Class<?> contClass = Class.forName("kotlin.coroutines.Continuation");
            final Method firstMethod = Class.forName("kotlinx.coroutines.flow.FlowKt")
                .getMethod("first", flowClass, contClass);
            Object result = runSuspendFunction2(new SuspendInvocation() {
                @Override
                public Object invoke(Object continuation) throws Exception {
                    return firstMethod.invoke(null, flow, continuation);
                }
            });
            if (result instanceof Boolean) {
                return (Boolean) result;
            }
        } catch (Exception ignored) {
        }
        return fallback;
    }

    private void recordActiveModpackSelection(final String packageName, final String modpackName) {
        try {
            final Class<?> settingsClass = Class.forName("com.bepinex.android.settings.AppSettings");
            final Object settingsInstance = settingsClass.getField("INSTANCE").get(null);
            final Class<?> contClass = Class.forName("kotlin.coroutines.Continuation");
            final Method setActive = settingsClass.getMethod(
                "setActiveModpack",
                Context.class,
                String.class,
                String.class,
                contClass
            );
            runSuspendFunction2(new SuspendInvocation() {
                @Override
                public Object invoke(Object continuation) throws Exception {
                    return setActive.invoke(
                        settingsInstance,
                        BppLauncherActivity.this,
                        packageName,
                        modpackName,
                        continuation
                    );
                }
            });
            final Method setLastModpacks = settingsClass.getMethod(
                "setLastLaunchedModpacks",
                Context.class,
                String.class,
                Set.class,
                contClass
            );
            final Set<String> singleton = Collections.singleton(modpackName);
            runSuspendFunction2(new SuspendInvocation() {
                @Override
                public Object invoke(Object continuation) throws Exception {
                    return setLastModpacks.invoke(
                        settingsInstance,
                        BppLauncherActivity.this,
                        packageName,
                        singleton,
                        continuation
                    );
                }
            });
        } catch (Exception ignored) {
        }
    }

    private interface SuspendInvocation {
        Object invoke(Object continuation) throws Exception;
    }

    private Object runSuspendFunction2(final SuspendInvocation call) throws Exception {
        Class<?> fn2Class = Class.forName("kotlin.jvm.functions.Function2");
        Class<?> ctxClass = Class.forName("kotlin.coroutines.CoroutineContext");
        Object block = Proxy.newProxyInstance(
            fn2Class.getClassLoader(),
            new Class<?>[] { fn2Class },
            new InvocationHandler() {
                @Override
                public Object invoke(Object proxy, Method method, Object[] args) throws Throwable {
                    if ("invoke".equals(method.getName()) && args != null && args.length == 2) {
                        return call.invoke(args[1]);
                    }
                    return null;
                }
            }
        );
        Method runBlocking = Class.forName("kotlinx.coroutines.BuildersKt")
            .getMethod("runBlocking$default", ctxClass, fn2Class, int.class, Object.class);
        return runBlocking.invoke(null, null, block, 1, null);
    }

    private void exportDiagnostics() {
        if (exportingDiagnostics || isFinishing() || isDestroyed()) return;
        exportingDiagnostics = true;
        if (btnExportDiagnostics != null) {
            btnExportDiagnostics.setEnabled(false);
            btnExportDiagnostics.setAlpha(0.55f);
            btnExportDiagnostics.setText("正在导出诊断日志…");
        }
        final String displaySnapshot = BppDiagnosticExporter.captureDisplaySnapshot(this);
        final File gameRoot = resolveGameRootDir(BppBundledModpackInstaller.TARGET_PACKAGE);
        final File internalWork = new File(getFilesDir(), "bpp-bundled-installer");
        diagnosticWorker.execute(new Runnable() {
            @Override
            public void run() {
                final BppDiagnosticExporter.Saved saved = BppDiagnosticExporter.exportAndSave(
                    BppLauncherActivity.this, gameRoot, internalWork, displaySnapshot);
                runOnUiThread(new Runnable() {
                    @Override
                    public void run() {
                        exportingDiagnostics = false;
                        if (btnExportDiagnostics != null) {
                            btnExportDiagnostics.setEnabled(true);
                            btnExportDiagnostics.setAlpha(1f);
                            btnExportDiagnostics.setText("导出诊断日志");
                        }
                        if (isDestroyed() || isFinishing()) return;
                        showExportResult(saved);
                    }
                });
            }
        });
    }

    private void showExportResult(BppDiagnosticExporter.Saved saved) {
        if (saved == null || !saved.ok) {
            String error = saved == null ? "未知错误" : saved.error;
            new AlertDialog.Builder(this)
                .setTitle("导出失败")
                .setMessage("诊断日志没有生成，启动器仍可继续使用。\n" + error)
                .setPositiveButton("关闭", null)
                .show();
            return;
        }
        if (tvExportInfo != null) {
            tvExportInfo.setVisibility(View.VISIBLE);
            tvExportInfo.setText("最近导出：" + saved.displayPath);
        }
        try {
            ClipboardManager clipboard = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
            if (clipboard != null) {
                clipboard.setPrimaryClip(ClipData.newPlainText("BPP diagnostics", saved.displayPath));
            }
        } catch (Throwable ignored) {
        }
        StringBuilder message = new StringBuilder();
        message.append("已保存到：\n").append(saved.displayPath)
            .append("\n大小：").append(saved.sizeBytes).append(" 字节")
            .append("\nSHA-256：").append(saved.sha256)
            .append("\n\n路径已复制。可在系统「下载」里找到该 zip。分享前请确认可以接受包内的设备标识与日志。");
        if (saved.warning != null && saved.warning.length() > 0) {
            message.append("\n\n").append(saved.warning);
        }
        AlertDialog.Builder builder = new AlertDialog.Builder(this)
            .setTitle("诊断日志已保存")
            .setMessage(message.toString())
            .setNegativeButton("关闭", null);
        if (saved.shareUri != null) {
            builder.setPositiveButton("分享", (dialog, which) -> shareDiagnostic(saved.shareUri));
        }
        builder.show();
        if (saved.shareUri != null) shareDiagnostic(saved.shareUri);
    }

    private void shareDiagnostic(Uri uri) {
        if (uri == null) return;
        try {
            Intent share = new Intent(Intent.ACTION_SEND);
            share.setType("application/zip");
            share.putExtra(Intent.EXTRA_STREAM, uri);
            share.putExtra(Intent.EXTRA_SUBJECT, "BPP Android 诊断日志");
            share.putExtra(Intent.EXTRA_TEXT, "BazaarPlusPlus Android 诊断包");
            share.setClipData(ClipData.newRawUri("BPP diagnostics", uri));
            share.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            Intent chooser = Intent.createChooser(share, "分享诊断日志");
            chooser.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            startActivity(chooser);
        } catch (Throwable error) {
            Toast.makeText(this, "无法打开分享面板，文件仍已保存", Toast.LENGTH_LONG).show();
        }
    }

    private void notePrepare(String outcome, String detail) {
        String text = detail == null ? "" : detail.replace('\n', ' ').replace('\r', ' ');
        if (text.length() > 500) text = text.substring(0, 500);
        BppDiagnosticSessionLog.note(
            this,
            "prepare-results.log",
            BppDiagnosticBundle.nowIso() + " outcome=" + outcome + " detail=" + text
        );
    }

    private void logError(String message, Throwable error) {
        try {
            Class<?> logClass = Class.forName("com.bepinex.android.BepInExLog");
            Field f = logClass.getField("INSTANCE");
            Object instance = f.get(null);
            logClass.getMethod("e", String.class, Throwable.class).invoke(instance, message, error);
        } catch (Exception ignored) {
        }
    }

    private int dp(int value) {
        return Math.round(
            TypedValue.applyDimension(
                TypedValue.COMPLEX_UNIT_DIP,
                value,
                getResources().getDisplayMetrics()
            )
        );
    }
}

