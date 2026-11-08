package com.bazzarhelper.android.ui;

import android.app.Activity;
import android.app.Dialog;
import android.graphics.Color;
import android.graphics.drawable.ColorDrawable;
import android.view.*;
import android.widget.*;
import android.widget.ScrollView;

/**
 * In-app dialog surface: same panel, border and chunky edge as the cards. The body scrolls while
 * the action buttons stay pinned, so a long record list never pushes "关闭" off screen.
 */
final class Sheet {
  private final Activity host;
  private final Dialog dialog;
  private final TextView message;
  private final LinearLayout actionArea;
  private final LinearLayout body;
  private final MaxScroll scroll;
  private boolean firstItem = true;

  Sheet(Activity host, String eyebrow, String title) {
    this.host = host;
    dialog = new Dialog(host);
    dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);

    LinearLayout root = Ui.column(host);
    root.setPadding(Ui.dp(20), Ui.dp(18), Ui.dp(20), Ui.dp(18 + 3));
    root.setBackground(new Ui.Depth(Ui.PANEL, Ui.LINE, Ui.edge(Ui.PANEL), 22, 3, 1.5f));

    LinearLayout header = Ui.column(host);
    if (eyebrow != null) {
      header.addView(Ui.eyebrow(host, eyebrow));
      header.addView(Ui.spacer(host, 6));
    }
    header.addView(Ui.title(host, title, 21, Ui.INK));
    message = Ui.body(host, "", Ui.BODY);
    message.setVisibility(View.GONE);
    LinearLayout.LayoutParams messageParams = new LinearLayout.LayoutParams(-1, -2);
    messageParams.topMargin = Ui.dp(10);
    header.addView(message, messageParams);
    root.addView(header, new LinearLayout.LayoutParams(-1, -2));

    body = Ui.column(host);
    scroll = new MaxScroll(host);
    scroll.setFillViewport(false);
    scroll.setClipToPadding(false);
    scroll.setVerticalScrollBarEnabled(false);
    scroll.maxHeight = host.getResources().getDisplayMetrics().heightPixels - Ui.dp(210);
    scroll.addView(body);
    LinearLayout.LayoutParams scrollParams = new LinearLayout.LayoutParams(-1, -2);
    scrollParams.topMargin = Ui.dp(6);
    root.addView(scroll, scrollParams);

    actionArea = Ui.column(host);
    LinearLayout.LayoutParams actionParams = new LinearLayout.LayoutParams(-1, -2);
    actionParams.topMargin = Ui.dp(6);
    root.addView(actionArea, actionParams);

    dialog.setContentView(root);
    dialog.setCanceledOnTouchOutside(true);
    Window window = dialog.getWindow();
    window.setBackgroundDrawable(new ColorDrawable(Color.TRANSPARENT));
    window.addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND);
    WindowManager.LayoutParams attrs = window.getAttributes();
    attrs.dimAmount = 0.62f;
    window.setAttributes(attrs);
    window.setGravity(Gravity.CENTER);
  }

  Sheet message(CharSequence value) {
    if (value == null || value.length() == 0) {
      message.setVisibility(View.GONE);
    } else {
      message.setText(value);
      message.setVisibility(View.VISIBLE);
    }
    return this;
  }

  Sheet body(View view) {
    ViewGroup.LayoutParams existing = view.getLayoutParams();
    LinearLayout.LayoutParams p =
        existing instanceof LinearLayout.LayoutParams
            ? (LinearLayout.LayoutParams) existing
            : new LinearLayout.LayoutParams(-1, -2);
    if (p.topMargin == 0) p.topMargin = Ui.dp(12);
    body.addView(view, p);
    return this;
  }

  /** A tappable list row (records, accounts) that dismisses before running its action. */
  Sheet item(View view, Runnable onClick) {
    if (!firstItem) {
      View line = new View(host);
      line.setBackgroundColor(Ui.alpha(Ui.LINE, 0.7f));
      LinearLayout.LayoutParams lineParams =
          new LinearLayout.LayoutParams(-1, Math.max(1, Ui.dp(1)));
      lineParams.leftMargin = Ui.dp(4);
      lineParams.rightMargin = Ui.dp(4);
      body.addView(line, lineParams);
    }
    firstItem = false;
    LinearLayout wrap = Ui.row(host);
    wrap.addView(view, new LinearLayout.LayoutParams(-1, -2));
    if (onClick != null) {
      Ui.pressable(wrap);
      wrap.setOnClickListener(
          v -> {
            dismiss();
            onClick.run();
          });
    }
    body.addView(wrap, new LinearLayout.LayoutParams(-1, -2));
    return this;
  }

  Sheet primary(String label, Runnable action) {
    return action(ChunkyButton.primary(host).label(label), action, true);
  }

  /** Like {@link #primary} but the sheet stays up, for actions that report back in place. */
  Sheet primaryStaysOpen(String label, Runnable action) {
    return action(ChunkyButton.primary(host).label(label), action, false);
  }

  Sheet secondary(String label, Runnable action) {
    return action(ChunkyButton.secondary(host).label(label), action, true);
  }

  Sheet danger(String label, Runnable action) {
    return action(ChunkyButton.danger(host).label(label), action, true);
  }

  private Sheet action(ChunkyButton button, Runnable action, boolean close) {
    LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(-1, -2);
    p.topMargin = Ui.dp(10);
    button.setOnClickListener(
        v -> {
          if (close) dismiss();
          if (action != null) action.run();
        });
    actionArea.addView(button, p);
    return this;
  }

  Sheet cancelable(boolean value) {
    dialog.setCancelable(value);
    dialog.setCanceledOnTouchOutside(value);
    return this;
  }

  void show() {
    if (dialog.isShowing()) return;
    dialog.show();
    Window window = dialog.getWindow();
    int width = Math.min(host.getResources().getDisplayMetrics().widthPixels - Ui.dp(40), Ui.dp(420));
    window.setLayout(width, WindowManager.LayoutParams.WRAP_CONTENT);
  }

  void dismiss() {
    if (dialog.isShowing()) dialog.dismiss();
  }

  boolean isShowing() {
    return dialog.isShowing();
  }

  /** Caps the scrolling body so header and actions always remain on screen. */
  static final class MaxScroll extends ScrollView {
    int maxHeight;

    MaxScroll(android.content.Context c) {
      super(c);
    }

    @Override
    protected void onMeasure(int widthSpec, int heightSpec) {
      super.onMeasure(widthSpec, heightSpec);
      if (maxHeight > 0 && getMeasuredHeight() > maxHeight)
        super.onMeasure(widthSpec, MeasureSpec.makeMeasureSpec(maxHeight, MeasureSpec.AT_MOST));
    }
  }
}
