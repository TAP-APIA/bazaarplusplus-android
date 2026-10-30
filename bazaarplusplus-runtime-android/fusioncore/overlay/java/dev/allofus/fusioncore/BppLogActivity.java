package dev.allofus.fusioncore;

import android.content.Intent;
import android.os.Bundle;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import androidx.appcompat.app.AppCompatActivity;
import androidx.core.content.FileProvider;

import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;

public class BppLogActivity extends AppCompatActivity {
    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        ScrollView scroll = new ScrollView(this);
        LinearLayout page = BppUi.page(this);
        scroll.addView(page);
        page.addView(BppUi.title(this, "运行日志"));
        File log = BppModRepository.logFile();
        page.addView(BppUi.button(this, "分享日志", v -> share(log)));
        TextView body = BppUi.text(this, read(log), 12, BppUi.TEXT);
        body.setTextIsSelectable(true);
        body.setTypeface(android.graphics.Typeface.MONOSPACE);
        page.addView(body);
        setContentView(scroll);
    }

    private String read(File file) {
        if (!file.isFile()) return "尚未生成 LogOutput.log。启动一次游戏后再查看。";
        try {
            byte[] bytes = Files.readAllBytes(file.toPath());
            int start = Math.max(0, bytes.length - 256 * 1024);
            return new String(bytes, start, bytes.length - start, StandardCharsets.UTF_8);
        } catch (Exception e) { return "读取失败：" + e.getMessage(); }
    }

    private void share(File file) {
        if (!file.isFile()) return;
        Intent intent = new Intent(Intent.ACTION_SEND);
        intent.setType("text/plain");
        intent.putExtra(Intent.EXTRA_STREAM, FileProvider.getUriForFile(this,
                getPackageName() + ".files", file));
        intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        startActivity(Intent.createChooser(intent, "分享 BepInEx 日志"));
    }
}
