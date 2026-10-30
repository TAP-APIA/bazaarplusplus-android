package dev.allofus.fusioncore;

import android.content.Intent;
import android.content.pm.PackageInfo;
import android.database.Cursor;
import android.graphics.Color;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.provider.OpenableColumns;
import android.provider.Settings;
import android.view.Menu;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Toast;

import androidx.activity.result.ActivityResultLauncher;
import androidx.activity.result.contract.ActivityResultContracts;
import androidx.appcompat.app.AlertDialog;
import androidx.appcompat.app.AppCompatActivity;
import androidx.appcompat.widget.SwitchCompat;

import com.google.android.material.bottomnavigation.BottomNavigationView;
import com.google.android.material.button.MaterialButton;

import java.io.IOException;

import dev.allofus.fusioncore.tools.CrashDetector;
import dev.allofus.fusioncore.tools.Utilities;

public class SelectorActivity extends AppCompatActivity {
    private static final int TAB_GAME = 1;
    private static final int TAB_MODS = 2;
    private static final int TAB_SETTINGS = 3;
    private static final int STORAGE_REQUEST = 1001;
    private int selectedTab = TAB_GAME;
    private LinearLayout content;
    private final ActivityResultLauncher<String[]> importer = registerForActivityResult(
            new ActivityResultContracts.OpenDocument(), this::importSelected);

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        buildShell();
        if (hasStorage()) CrashDetector.init(this);
    }

    @Override protected void onResume() {
        super.onResume();
        if (content != null) renderSelected();
    }

    private void buildShell() {
        LinearLayout shell = new LinearLayout(this);
        shell.setOrientation(LinearLayout.VERTICAL);
        shell.setBackgroundColor(BppUi.BG);
        ScrollView scroll = new ScrollView(this);
        content = BppUi.page(this);
        scroll.addView(content);
        shell.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));

        BottomNavigationView navigation = new BottomNavigationView(this);
        navigation.setBackgroundColor(BppUi.SURFACE);
        Menu menu = navigation.getMenu();
        menu.add(Menu.NONE, TAB_GAME, Menu.NONE, "游戏").setIcon(android.R.drawable.ic_media_play);
        menu.add(Menu.NONE, TAB_MODS, Menu.NONE, "Mod").setIcon(android.R.drawable.ic_menu_add);
        menu.add(Menu.NONE, TAB_SETTINGS, Menu.NONE, "设置").setIcon(android.R.drawable.ic_menu_preferences);
        navigation.setOnItemSelectedListener(item -> {
            selectedTab = item.getItemId();
            renderSelected();
            return true;
        });
        navigation.setSelectedItemId(TAB_GAME);
        shell.addView(navigation, new LinearLayout.LayoutParams(-1, BppUi.dp(this, 72)));
        setContentView(shell);
        Utilities.applyWindowInsets(shell, 0);
    }

    private void renderSelected() {
        content.removeAllViews();
        if (selectedTab == TAB_MODS) renderMods();
        else if (selectedTab == TAB_SETTINGS) renderSettings();
        else renderGame();
    }

    private void renderGame() {
        content.addView(BppUi.title(this, "BHL1"));
        content.addView(BppUi.text(this, "Bazaar HarmonyOS Launcher", 14, BppUi.MUTED));
        LinearLayout game = BppUi.panel(this);
        boolean installed = isGameInstalled();
        game.addView(BppUi.text(this, "The Bazaar", 19, BppUi.TEXT));
        game.addView(BppUi.text(this, installed ? gameVersion() : "未安装官方游戏", 13,
                installed ? BppUi.ACCENT : Color.RED));
        game.addView(BppUi.text(this, "BazaarPlusPlus " +
                (BppModRepository.isBuiltInEnabled(this) ? "已启用" : "已禁用"), 13, BppUi.MUTED));
        MaterialButton launch = BppUi.button(this, "启动游戏", v -> launchGame());
        launch.setEnabled(installed);
        game.addView(launch);
        content.addView(game);
    }

    private void renderMods() {
        content.addView(BppUi.title(this, "Mod"));
        content.addView(BppUi.text(this, "管理内置和用户导入的托管插件", 13, BppUi.MUTED));
        LinearLayout builtin = BppUi.panel(this);
        builtin.addView(BppUi.text(this, "BazaarPlusPlus", 18, BppUi.TEXT));
        builtin.addView(BppUi.text(this, "内置 Mod · 随 BHL1 更新", 13, BppUi.MUTED));
        SwitchCompat enabled = switchRow("启用", BppModRepository.isBuiltInEnabled(this));
        enabled.setOnCheckedChangeListener((button, checked) -> BppModRepository.setBuiltInEnabled(this, checked));
        builtin.addView(enabled);
        content.addView(builtin);
        content.addView(BppUi.button(this, "导入 Mod", v -> importer.launch(new String[]{
                "application/zip", "application/x-zip-compressed", "application/octet-stream"
        })));
        for (BppModRepository.ImportedMod mod : BppModRepository.listImported()) addImported(mod);
    }

    private void addImported(BppModRepository.ImportedMod mod) {
        LinearLayout panel = BppUi.panel(this);
        panel.addView(BppUi.text(this, mod.name, 17, BppUi.TEXT));
        panel.addView(BppUi.text(this, mod.enabled ? "已启用" : "已禁用", 13,
                mod.enabled ? BppUi.ACCENT : BppUi.MUTED));
        panel.addView(BppUi.button(this, mod.enabled ? "禁用" : "启用", v -> {
            if (!BppModRepository.setEnabled(mod, !mod.enabled)) toast("无法修改 Mod 状态");
            renderSelected();
        }));
        panel.addView(BppUi.button(this, "删除", v -> new AlertDialog.Builder(this)
                .setTitle("删除 " + mod.name + "？")
                .setMessage("只删除该 Mod 文件，不删除游戏数据。")
                .setNegativeButton("取消", null)
                .setPositiveButton("删除", (dialog, which) -> { BppModRepository.delete(mod); renderSelected(); })
                .show()));
        content.addView(panel);
    }

    private void renderSettings() {
        content.addView(BppUi.title(this, "设置"));
        content.addView(BppUi.text(this, "启动器、兼容性与诊断", 13, BppUi.MUTED));
        LinearLayout compatibility = BppUi.panel(this);
        compatibility.addView(BppUi.text(this, "游戏启动", 17, BppUi.TEXT));
        SwitchCompat unity = switchRow("下载匹配的 Unity 基础库",
                FusionSettings.getUseUnstrippedLibUnityForGame(this, BppModRepository.GAME_PACKAGE));
        unity.setOnCheckedChangeListener((v, checked) -> FusionSettings.setUseUnstrippedLibUnityForGame(
                this, BppModRepository.GAME_PACKAGE, checked));
        compatibility.addView(unity);
        SwitchCompat mono = switchRow("IL2CPP2Mono（实验性）",
                FusionSettings.getUseIl2Cpp2Mono(this, BppModRepository.GAME_PACKAGE));
        mono.setOnCheckedChangeListener((v, checked) -> FusionSettings.setUseIl2Cpp2Mono(
                this, BppModRepository.GAME_PACKAGE, checked));
        compatibility.addView(mono);
        compatibility.addView(BppUi.button(this, "高级启动设置", v -> {
            Intent intent = new Intent(this, GameSettingsActivity.class);
            intent.putExtra(GameSettingsActivity.EXTRA_PACKAGE_NAME, BppModRepository.GAME_PACKAGE);
            startActivity(intent);
        }));
        content.addView(compatibility);

        LinearLayout diagnostics = BppUi.panel(this);
        diagnostics.addView(BppUi.text(this, "诊断与文件", 17, BppUi.TEXT));
        diagnostics.addView(BppUi.button(this, "查看运行日志", v -> startActivity(new Intent(this, BppLogActivity.class))));
        diagnostics.addView(BppUi.button(this, "打开本地文件", v -> openDataFolder()));
        content.addView(diagnostics);

        LinearLayout about = BppUi.panel(this);
        about.addView(BppUi.text(this, "关于 BHL1", 17, BppUi.TEXT));
        about.addView(BppUi.button(this, "GitHub 源代码", v -> openUrl("https://github.com/z492016389/bazaarplusplus-android")));
        about.addView(BppUi.button(this, "鸣谢与许可证", v -> showCredits()));
        content.addView(about);
    }

    private SwitchCompat switchRow(String label, boolean checked) {
        SwitchCompat view = new SwitchCompat(this);
        view.setText(label);
        view.setTextColor(BppUi.TEXT);
        view.setChecked(checked);
        view.setPadding(0, BppUi.dp(this, 10), 0, BppUi.dp(this, 10));
        return view;
    }

    private boolean isGameInstalled() {
        try { getPackageManager().getPackageInfo(BppModRepository.GAME_PACKAGE, 0); return true; }
        catch (Exception ignored) { return false; }
    }
    private String gameVersion() {
        try {
            PackageInfo info = getPackageManager().getPackageInfo(BppModRepository.GAME_PACKAGE, 0);
            return "版本 " + info.versionName;
        } catch (Exception ignored) { return "已安装"; }
    }
    private void launchGame() {
        if (!hasStorage()) { requestStorage(); return; }
        Intent intent = new Intent(this, BootstrapActivity.class);
        intent.putExtra(BootstrapActivity.EXTRA_TARGET_PACKAGE, BppModRepository.GAME_PACKAGE);
        intent.putExtra(BootstrapActivity.EXTRA_USE_ORIGINAL_LIBUNITY,
                !FusionSettings.getUseUnstrippedLibUnityForGame(this, BppModRepository.GAME_PACKAGE));
        intent.putExtra(BootstrapActivity.EXTRA_USE_IL2CPP2MONO,
                FusionSettings.getUseIl2Cpp2Mono(this, BppModRepository.GAME_PACKAGE));
        intent.addFlags(Intent.FLAG_ACTIVITY_NO_ANIMATION | Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        startActivity(intent);
        finish();
    }
    private boolean hasStorage() { return Build.VERSION.SDK_INT < 30 || Environment.isExternalStorageManager(); }
    private void requestStorage() {
        if (Build.VERSION.SDK_INT >= 30) startActivityForResult(new Intent(
                Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION,
                Uri.parse("package:" + getPackageName())), STORAGE_REQUEST);
    }
    private void openDataFolder() {
        try {
            Uri uri = Uri.parse("content://com.android.externalstorage.documents/document/primary%3AFusionCore%2F" +
                    Uri.encode(BppModRepository.GAME_PACKAGE));
            startActivity(new Intent(Intent.ACTION_VIEW).setDataAndType(uri, "vnd.android.document/directory"));
        } catch (Exception e) { toast("文件管理器无法打开该目录"); }
    }
    private void openUrl(String url) {
        try { startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(url))); }
        catch (Exception e) { toast("无法打开浏览器"); }
    }
    private void showCredits() {
        String credits = "游戏与商标：Tempo Games / The Bazaar\n\n" +
                "Mod：BazaarPlusPlus 项目及其贡献者\n" +
                "Android 移植与本仓库：TAP-APIA\n" +
                "运行框架：All-Of-Us-Mods / FusionCore（GPL-3.0）\n" +
                "插件框架：BepInEx\n互操作：Il2CppInterop\n补丁框架：Harmony\n\n" +
                "实现协助：OpenAI ChatGPT / Codex\n\n" +
                "感谢所有上游作者、贡献者和测试用户。各项目版权与许可证归其各自所有者。";
        new AlertDialog.Builder(this).setTitle("鸣谢与许可证").setMessage(credits)
                .setPositiveButton("关闭", null).show();
    }
    private void importSelected(Uri uri) {
        if (uri == null) return;
        try { toast("已导入 " + BppModRepository.importUri(this, uri, displayName(uri))); renderSelected(); }
        catch (IOException e) { toast("导入失败：" + e.getMessage()); }
    }
    private String displayName(Uri uri) {
        try (Cursor cursor = getContentResolver().query(uri, null, null, null, null)) {
            if (cursor != null && cursor.moveToFirst()) {
                int column = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME);
                if (column >= 0) return cursor.getString(column);
            }
        }
        String last = uri.getLastPathSegment();
        return last == null ? "imported-mod.zip" : last;
    }
    private void toast(String text) { Toast.makeText(this, text, Toast.LENGTH_LONG).show(); }
}
