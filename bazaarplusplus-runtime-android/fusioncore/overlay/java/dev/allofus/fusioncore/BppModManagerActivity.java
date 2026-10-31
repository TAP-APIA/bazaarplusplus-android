package dev.allofus.fusioncore;

import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.Bundle;
import android.provider.OpenableColumns;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Toast;

import androidx.activity.result.ActivityResultLauncher;
import androidx.activity.result.contract.ActivityResultContracts;
import androidx.appcompat.app.AlertDialog;
import androidx.appcompat.app.AppCompatActivity;
import androidx.appcompat.widget.SwitchCompat;

import java.io.IOException;

public class BppModManagerActivity extends AppCompatActivity {
    private LinearLayout page;
    private final ActivityResultLauncher<String[]> importer = registerForActivityResult(
            new ActivityResultContracts.OpenDocument(), this::importSelected);

    @Override protected void onCreate(Bundle state) { super.onCreate(state); render(); }

    private void render() {
        ScrollView scroll = new ScrollView(this);
        page = BppUi.page(this);
        scroll.addView(page);
        page.addView(BppUi.title(this, "Mod 管理"));
        page.addView(BppUi.text(this, "修改后请重新启动游戏", 13, BppUi.MUTED));

        LinearLayout builtin = BppUi.panel(this);
        builtin.addView(BppUi.text(this, "BazaarPlusPlus", 18, BppUi.TEXT));
        builtin.addView(BppUi.text(this, "内置 Mod · 随启动器更新", 13, BppUi.MUTED));
        SwitchCompat enabled = new SwitchCompat(this);
        enabled.setText("启用");
        enabled.setTextColor(BppUi.TEXT);
        enabled.setChecked(BppModRepository.isBuiltInEnabled(this));
        enabled.setOnCheckedChangeListener((button, checked) -> BppModRepository.setBuiltInEnabled(this, checked));
        builtin.addView(enabled);
        page.addView(builtin);

        page.addView(BppUi.button(this, "导入 Mod", v -> importer.launch(new String[]{
                "application/zip", "application/x-zip-compressed", "application/octet-stream"
        })));

        for (BppModRepository.ImportedMod mod : BppModRepository.listImported()) addImported(mod);
        setContentView(scroll);
    }

    private void addImported(BppModRepository.ImportedMod mod) {
        LinearLayout panel = BppUi.panel(this);
        panel.addView(BppUi.text(this, mod.name, 17, BppUi.TEXT));
        panel.addView(BppUi.text(this, mod.enabled ? "已启用" : "已禁用", 13,
                mod.enabled ? BppUi.ACCENT : BppUi.MUTED));
        panel.addView(BppUi.button(this, mod.enabled ? "禁用" : "启用", v -> {
            if (!BppModRepository.setEnabled(mod, !mod.enabled)) toast("无法修改 Mod 状态");
            render();
        }));
        panel.addView(BppUi.button(this, "删除", v -> new AlertDialog.Builder(this)
                .setTitle("删除 " + mod.name + "？")
                .setMessage("只删除该 Mod 文件，不删除游戏或 BazaarPlusPlus 历史数据。")
                .setNegativeButton("取消", null)
                .setPositiveButton("删除", (dialog, which) -> { BppModRepository.delete(mod); render(); })
                .show()));
        page.addView(panel);
    }

    private void importSelected(Uri uri) {
        if (uri == null) return;
        String name = displayName(uri);
        try {
            String imported = BppModRepository.importUri(this, uri, name);
            toast("已导入 " + imported);
            render();
        } catch (IOException e) { toast("导入失败：" + e.getMessage()); }
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
