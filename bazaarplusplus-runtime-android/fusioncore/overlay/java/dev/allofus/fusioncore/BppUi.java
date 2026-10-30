package dev.allofus.fusioncore;

import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.view.View;
import android.widget.LinearLayout;
import android.widget.TextView;

import com.google.android.material.button.MaterialButton;

final class BppUi {
    static final int BG = Color.rgb(18, 20, 22);
    static final int SURFACE = Color.rgb(31, 34, 37);
    static final int TEXT = Color.rgb(239, 240, 241);
    static final int MUTED = Color.rgb(172, 178, 183);
    static final int ACCENT = Color.rgb(82, 183, 136);

    private BppUi() {}
    static int dp(Context c, int value) { return Math.round(value * c.getResources().getDisplayMetrics().density); }

    static LinearLayout page(Context c) {
        LinearLayout page = new LinearLayout(c);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(c, 20), dp(c, 16), dp(c, 20), dp(c, 24));
        page.setBackgroundColor(BG);
        return page;
    }

    static TextView title(Context c, String text) {
        TextView view = text(c, text, 25, TEXT);
        view.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        return view;
    }

    static TextView text(Context c, String text, int sp, int color) {
        TextView view = new TextView(c);
        view.setText(text);
        view.setTextSize(sp);
        view.setTextColor(color);
        view.setPadding(0, dp(c, 4), 0, dp(c, 4));
        return view;
    }

    static LinearLayout panel(Context c) {
        LinearLayout panel = new LinearLayout(c);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(c, 16), dp(c, 14), dp(c, 16), dp(c, 14));
        panel.setBackgroundColor(SURFACE);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.setMargins(0, dp(c, 12), 0, 0);
        panel.setLayoutParams(params);
        return panel;
    }

    static MaterialButton button(Context c, String label, View.OnClickListener listener) {
        MaterialButton button = new MaterialButton(c);
        button.setText(label);
        button.setAllCaps(false);
        button.setLetterSpacing(0);
        button.setOnClickListener(listener);
        return button;
    }
}
