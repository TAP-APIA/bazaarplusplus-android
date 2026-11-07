package com.bazzarhelper.android.ui;

import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.*;
import android.view.MotionEvent;
import android.view.View;
import android.view.accessibility.AccessibilityNodeInfo;

/**
 * Duolingo-style action button: solid body sitting on a darker bottom edge, and the body sinks
 * onto that edge while pressed. Drawn in code so tint, label size and depth stay one source of
 * truth; height follows the font scale instead of clipping large text.
 */
final class ChunkyButton extends View {
  private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
  private final Paint label = new Paint(Paint.ANTI_ALIAS_FLAG);
  private final RectF rect = new RectF();
  private final float minBodyDp;

  private String text = "";
  private Ui.Glyph glyph;
  private int fill = Ui.ACCENT, edge = Ui.edge(Ui.ACCENT), border = Ui.ALPHA_NONE,
      labelColor = Ui.ON_ACCENT;
  private float radiusDp = 16, depthDp = 4, textSizeSp = 15.5f, glyphDp = 20, gapDp = 9;
  private float press;
  private ValueAnimator animator;
  private boolean down;

  ChunkyButton(Context c) {
    this(c, 52);
  }

  ChunkyButton(Context c, float bodyDp) {
    super(c);
    minBodyDp = bodyDp;
    setFocusable(true);
    setClickable(true);
    label.setTextAlign(Paint.Align.CENTER);
    label.setTypeface(Ui.strong());
    setSoundEffectsEnabled(true);
  }

  static ChunkyButton primary(Context c) {
    return new ChunkyButton(c);
  }

  static ChunkyButton secondary(Context c) {
    ChunkyButton b = new ChunkyButton(c, 46);
    b.fill = Ui.RAISED;
    b.edge = Ui.edge(Ui.RAISED);
    b.border = Ui.LINE;
    b.labelColor = Ui.ACCENT;
    b.textSizeSp = 14.5f;
    return b;
  }

  static ChunkyButton danger(Context c) {
    ChunkyButton b = secondary(c);
    b.labelColor = Ui.LOSS;
    b.border = Ui.alpha(Ui.LOSS, 0.4f);
    return b;
  }

  ChunkyButton label(String value) {
    text = value;
    setContentDescription(value);
    requestLayout();
    invalidate();
    return this;
  }

  ChunkyButton icon(Ui.Glyph value) {
    glyph = value;
    invalidate();
    return this;
  }

  @Override
  protected void onMeasure(int widthSpec, int heightSpec) {
    label.setTextSize(Ui.spToPx(textSizeSp));
    float textWidth = label.measureText(text);
    float glyphSize = glyph == null ? 0 : Ui.dp(glyphDp);
    float gap = glyph == null ? 0 : Ui.dp(gapDp);
    int bodyHeight =
        Math.max(Ui.dp(minBodyDp), Math.round(-label.ascent() + label.descent()) + Ui.dp(22));
    int desiredWidth = Math.round(textWidth + glyphSize + gap + Ui.dp(44));
    int width = resolveSize(Math.max(desiredWidth, Ui.dp(112)), widthSpec);
    int height = resolveSize(bodyHeight + Ui.dp(depthDp), heightSpec);
    setMeasuredDimension(width, height);
  }

  @Override
  protected void onDraw(Canvas canvas) {
    float w = getWidth(), h = getHeight();
    float depth = Ui.dp(depthDp), r = Ui.dp(radiusDp);
    float sink = press * depth;
    float bodyBottom = h - depth;

    paint.setStyle(Paint.Style.FILL);
    paint.setColor(edge);
    rect.set(0, depth, w, h);
    canvas.drawRoundRect(rect, r, r, paint);

    paint.setColor(fill);
    rect.set(0, sink, w, bodyBottom + sink);
    canvas.drawRoundRect(rect, r, r, paint);

    if (border != Ui.ALPHA_NONE) {
      float width = Ui.dp(1.4f);
      paint.setStyle(Paint.Style.STROKE);
      paint.setStrokeWidth(width);
      paint.setColor(border);
      rect.set(width / 2f, sink + width / 2f, w - width / 2f, bodyBottom + sink - width / 2f);
      canvas.drawRoundRect(rect, r - width / 2f, r - width / 2f, paint);
    }

    float centerY = sink + bodyBottom / 2f;
    float textWidth = label.measureText(text);
    float glyphSize = glyph == null ? 0 : Ui.dp(glyphDp);
    float gap = glyph == null ? 0 : Ui.dp(gapDp);
    float start = (w - textWidth - glyphSize - gap) / 2f;
    label.setColor(isEnabled() ? labelColor : Ui.MUTED);
    canvas.drawText(
        text,
        start + glyphSize + gap + textWidth / 2f,
        centerY - (label.ascent() + label.descent()) / 2f,
        label);
    if (glyph != null)
      Ui.drawGlyph(
          canvas,
          glyph,
          start + glyphSize / 2f,
          centerY,
          glyphSize,
          isEnabled() ? labelColor : Ui.MUTED);
  }

  @Override
  public void setEnabled(boolean enabled) {
    super.setEnabled(enabled);
    if (!enabled) animateTo(0);
    invalidate();
  }

  @Override
  public boolean onTouchEvent(MotionEvent event) {
    if (!isEnabled()) return false;
    switch (event.getActionMasked()) {
      case MotionEvent.ACTION_DOWN:
        down = true;
        setPressed(true);
        animateTo(1);
        return true;
      case MotionEvent.ACTION_MOVE:
        boolean inside = contains(event.getX(), event.getY());
        if (inside != down) {
          down = inside;
          animateTo(inside ? 1 : 0);
        }
        return true;
      case MotionEvent.ACTION_UP:
        boolean tap = down && contains(event.getX(), event.getY());
        down = false;
        setPressed(false);
        animateTo(0);
        if (tap) performClick();
        return true;
      case MotionEvent.ACTION_CANCEL:
        down = false;
        setPressed(false);
        animateTo(0);
        return true;
      default:
        return true;
    }
  }

  @Override
  public boolean performClick() {
    return super.performClick();
  }

  @Override
  public void onInitializeAccessibilityNodeInfo(AccessibilityNodeInfo info) {
    super.onInitializeAccessibilityNodeInfo(info);
    info.setClassName("android.widget.Button");
    info.setText(text);
    info.setClickable(true);
  }

  private boolean contains(float x, float y) {
    return x >= 0 && y >= 0 && x <= getWidth() && y <= getHeight();
  }

  private void animateTo(float target) {
    if (animator != null) animator.cancel();
    if (!ValueAnimator.areAnimatorsEnabled() || Math.abs(target - press) < 0.001f) {
      press = target;
      invalidate();
      return;
    }
    animator = ValueAnimator.ofFloat(press, target);
    animator.setDuration(target > press ? 70 : 130);
    animator.addUpdateListener(
        a -> {
          press = (float) a.getAnimatedValue();
          invalidate();
        });
    animator.start();
  }
}
