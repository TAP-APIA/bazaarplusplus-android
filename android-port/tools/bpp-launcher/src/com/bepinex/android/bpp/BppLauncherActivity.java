package com.bepinex.android.bpp;

import android.app.Activity;
import android.app.ActivityManager;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.os.Process;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
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

    private volatile boolean busy;
    private volatile boolean autoLaunchConsumed;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        initLauncherCore();
        setContentView(buildContentView());
    }

    @Override
    protected void onResume() {
        super.onResume();
        clearPendingCrashBanner();
        boolean requestAutoLaunch = !autoLaunchConsumed
            && getIntent() != null
            && getIntent().getBooleanExtra(EXTRA_AUTO_LAUNCH, false);
        if (requestAutoLaunch) {
            autoLaunchConsumed = true;
        }
        startPrepareFlow(false, requestAutoLaunch, false);
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        worker.shutdownNow();
    }

    private View buildContentView() {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(COLOR_BG);

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
                startPrepareFlow(false, true, false);
            }
        });
        LinearLayout.LayoutParams launchLp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            dp(54)
        );
        launchLp.topMargin = dp(16);
        root.addView(btnLaunch, launchLp);

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

        scroll.addView(root);
        return scroll;
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

            if (launchWhenReady) {
                launchBazaarWithBpp(pkg);
            }
        } catch (final Exception ex) {
            logError("BPP bundled modpack preparation failed", ex);
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
