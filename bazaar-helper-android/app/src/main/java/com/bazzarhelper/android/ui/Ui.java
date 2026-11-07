package com.bazzarhelper.android.ui;

import android.content.Context;
import android.content.res.Resources;
import android.graphics.*;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.*;

/**
 * Shared visual language. Palette comes from the catalogue theme (miniprogram/theme.wxss); the
 * chunky bottom edge, thick card outline and pill chips follow Duolingo's component rhythm.
 */
final class Ui {
  static final int BG = 0xff101c26,
      NAV = 0xff0d1720,
      PANEL = 0xff1b2b36,
      RAISED = 0xff263c48,
      CHIP = 0xff253a46,
      LINE = 0xff3c5360,
      HERO = 0xff2b454c,
      INK = 0xffecede4,
      BODY = 0xffd6dfe0,
      MUTED = 0xffa0b2bb,
      ACCENT = 0xffe8b95d,
      ON_ACCENT = 0xff14232d,
      WIN = 0xff73cbb0,
      LOSS = 0xfff2a194;

  /** Glyphs are drawn, not bundled, so they follow the tint and never ship artwork. */
  enum Glyph {
    ACCOUNT,
    PLAY,
    CLOCK,
    RECORDS,
    SYNC,
    EXPORT
  }

  private Ui() {}

  static float density() {
    return Resources.getSystem().getDisplayMetrics().density;
  }

  static int dp(float value) {
    return Math.round(value * density());
  }

  static float spToPx(float value) {
    return value * Resources.getSystem().getDisplayMetrics().scaledDensity;
  }

  static int edge(int color) {
    return blend(color, 0xff000000, 0.34f);
  }

  static int alpha(int color, float factor) {
    return (color & 0x00ffffff) | (Math.round(255 * factor) << 24);
  }

  static int blend(int a, int b, float t) {
    int ar = (a >> 16) & 0xff, ag = (a >> 8) & 0xff, ab = a & 0xff;
    int br = (b >> 16) & 0xff, bg = (b >> 8) & 0xff, bb = b & 0xff;
    return 0xff000000
        | (Math.round(ar + (br - ar) * t) << 16)
        | (Math.round(ag + (bg - ag) * t) << 8)
        | Math.round(ab + (bb - ab) * t);
  }

  static Typeface typeface(String family) {
    return Typeface.create(family, Typeface.NORMAL);
  }

  static Typeface title() {
    return typeface("sans-serif-black");
  }

  static Typeface strong() {
    return typeface("sans-serif-medium");
  }

  // ---- text ----

  static TextView text(Context c, CharSequence value, float size, int color, Typeface face) {
    TextView t = new TextView(c);
    t.setText(value);
    t.setTextSize(size);
    t.setTextColor(color);
    t.setTypeface(face);
    t.setIncludeFontPadding(false);
    return t;
  }

  static TextView title(Context c, String value, float size, int color) {
    return text(c, value, size, color, title());
  }

  static TextView body(Context c, String value, int color) {
    TextView t = text(c, value, 14, color, Typeface.DEFAULT);
    t.setLineSpacing(dp(3), 1f);
    return t;
  }

  static TextView eyebrow(Context c, String value) {
    TextView t = text(c, value.toUpperCase(), 10, ACCENT, strong());
    t.setLetterSpacing(0.16f);
    t.setAllCaps(false);
    return t;
  }

  static TextView muted(Context c, String value) {
    return text(c, value, 12.5f, MUTED, Typeface.DEFAULT);
  }

  // ---- layout ----

  static LinearLayout column(Context c) {
    LinearLayout l = new LinearLayout(c);
    l.setOrientation(LinearLayout.VERTICAL);
    return l;
  }

  static LinearLayout row(Context c) {
    LinearLayout l = new LinearLayout(c);
    l.setOrientation(LinearLayout.HORIZONTAL);
    l.setGravity(Gravity.CENTER_VERTICAL);
    return l;
  }

  static View spacer(Context c, int dp) {
    View v = new View(c);
    v.setLayoutParams(new LinearLayout.LayoutParams(1, dp(dp)));
    return v;
  }

  static View divider(Context c) {
    View v = new View(c);
    v.setBackgroundColor(LINE);
    LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(-1, Math.max(1, dp(1)));
    p.topMargin = dp(14);
    p.bottomMargin = dp(12);
    v.setLayoutParams(p);
    return v;
  }

  static LinearLayout.LayoutParams lp(int width, int height) {
    return new LinearLayout.LayoutParams(width, height);
  }

  // ---- surfaces ----

  static Drawable depth(int fill, int stroke, float radiusDp, float depthDp) {
    return new Depth(fill, stroke, ALPHA_NONE, radiusDp, depthDp, 1.5f);
  }

  static final int ALPHA_NONE = -1;

  static LinearLayout card(Context c) {
    LinearLayout l = column(c);
    l.setPadding(dp(16), dp(14), dp(16), dp(14 + 3));
    l.setBackground(new Depth(PANEL, LINE, edge(PANEL), 18, 3, 1.5f));
    return l;
  }

  static void cardIn(LinearLayout parent, LinearLayout card) {
    LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(-1, -2);
    p.bottomMargin = dp(12);
    parent.addView(card, p);
  }

  static TextView pill(Context c, String label, int color) {
    TextView t = text(c, label, 11.5f, color, strong());
    t.setPadding(dp(10), dp(4), dp(10), dp(5));
    t.setBackground(round(CHIP, 10));
    return t;
  }

  static GradientDrawable round(int fill, float radiusDp) {
    GradientDrawable g = new GradientDrawable();
    g.setColor(fill);
    g.setCornerRadius(dp(radiusDp));
    return g;
  }

  static void pressable(View v) {
    TypedValue out = new TypedValue();
    if (v.getContext().getTheme().resolveAttribute(android.R.attr.selectableItemBackground, out, true))
      v.setBackgroundResource(out.resourceId);
    v.setClickable(true);
    v.setFocusable(true);
  }

  // ---- glyphs ----

  static View tile(Context c, Glyph glyph, int tileDp, int tileColor, int glyphColor) {
    GlyphView v = new GlyphView(c);
    v.glyph = glyph;
    v.tileDp = tileDp;
    v.tileColor = tileColor;
    v.glyphColor = glyphColor;
    int size = dp(tileDp);
    v.setLayoutParams(new LinearLayout.LayoutParams(size, size));
    return v;
  }

  static void drawGlyph(Canvas canvas, Glyph glyph, float cx, float cy, float size, int color) {
    Paint p = new Paint(Paint.ANTI_ALIAS_FLAG);
    p.setColor(color);
    p.setStrokeWidth(Math.max(dp(1.4f), size * 0.1f));
    p.setStrokeCap(Paint.Cap.ROUND);
    p.setStrokeJoin(Paint.Join.ROUND);
    p.setStyle(Paint.Style.STROKE);
    float s = size;
    switch (glyph) {
      case PLAY:
        {
          Path path = new Path();
          path.moveTo(cx - s * 0.24f, cy - s * 0.32f);
          path.lineTo(cx + s * 0.32f, cy);
          path.lineTo(cx - s * 0.24f, cy + s * 0.32f);
          path.close();
          p.setStyle(Paint.Style.FILL);
          canvas.drawPath(path, p);
          break;
        }
      case ACCOUNT:
        {
          p.setStyle(Paint.Style.FILL);
          canvas.drawCircle(cx, cy - s * 0.16f, s * 0.17f, p);
          p.setStyle(Paint.Style.STROKE);
          canvas.drawArc(
              new RectF(cx - s * 0.31f, cy - s * 0.02f, cx + s * 0.31f, cy + s * 0.58f),
              200,
              140,
              false,
              p);
          break;
        }
      case CLOCK:
        {
          canvas.drawCircle(cx, cy, s * 0.34f, p);
          canvas.drawLine(cx, cy, cx, cy - s * 0.19f, p);
          canvas.drawLine(cx, cy, cx + s * 0.15f, cy + s * 0.06f, p);
          break;
        }
      case RECORDS:
        {
          float left = cx - s * 0.3f;
          for (int i = 0; i < 3; i++) {
            float y = cy + (i - 1) * s * 0.24f;
            canvas.drawLine(left, y, left + s * (0.38f + i * 0.09f), y, p);
          }
          break;
        }
      case EXPORT:
        {
          Path tray = new Path();
          tray.moveTo(cx - s * 0.30f, cy + s * 0.02f);
          tray.lineTo(cx - s * 0.30f, cy + s * 0.28f);
          tray.lineTo(cx + s * 0.30f, cy + s * 0.28f);
          tray.lineTo(cx + s * 0.30f, cy + s * 0.02f);
          canvas.drawPath(tray, p);
          canvas.drawLine(cx, cy + s * 0.12f, cx, cy - s * 0.32f, p);
          Path head = new Path();
          head.moveTo(cx - s * 0.16f, cy - s * 0.14f);
          head.lineTo(cx, cy - s * 0.32f);
          head.lineTo(cx + s * 0.16f, cy - s * 0.14f);
          canvas.drawPath(head, p);
          break;
        }
      case SYNC:
        {
          RectF arc = new RectF(cx - s * 0.3f, cy - s * 0.3f, cx + s * 0.3f, cy + s * 0.3f);
          canvas.drawArc(arc, 40, 250, false, p);
          p.setStyle(Paint.Style.FILL);
          Path head = new Path();
          head.moveTo(cx + s * 0.02f, cy - s * 0.38f);
          head.lineTo(cx + s * 0.34f, cy - s * 0.2f);
          head.lineTo(cx + s * 0.02f, cy - s * 0.04f);
          head.close();
          canvas.drawPath(head, p);
          break;
        }
    }
  }

  private static final class GlyphView extends View {
    Glyph glyph = Glyph.ACCOUNT;
    int tileColor = ALPHA_NONE, glyphColor = INK;
    int tileDp = 40;
    private final Paint fill = new Paint(Paint.ANTI_ALIAS_FLAG);

    GlyphView(Context c) {
      super(c);
      setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_NO);
    }

    @Override
    protected void onDraw(Canvas canvas) {
      float w = getWidth(), h = getHeight();
      if (tileColor != ALPHA_NONE) {
        fill.setColor(tileColor);
        float r = dp(tileDp * 0.32f);
        canvas.drawRoundRect(new RectF(0, 0, w, h), r, r, fill);
        fill.setColor(alpha(glyphColor, 0.16f));
        fill.setStyle(Paint.Style.STROKE);
        fill.setStrokeWidth(dp(1));
        canvas.drawRoundRect(new RectF(dp(0.5f), dp(0.5f), w - dp(0.5f), h - dp(0.5f)), r, r, fill);
        fill.setStyle(Paint.Style.FILL);
      }
      drawGlyph(canvas, glyph, w / 2f, h / 2f, Math.min(w, h) * (tileColor == ALPHA_NONE ? 1f : 0.56f), glyphColor);
    }

    @Override
    protected void onMeasure(int width, int height) {
      setMeasuredDimension(resolveSize(dp(tileDp), width), resolveSize(dp(tileDp), height));
    }
  }

  /** Rounded surface with a Duolingo-style solid bottom edge instead of a blur shadow. */
  static final class Depth extends Drawable {
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF rect = new RectF();
    private final int fill, stroke, edge;
    private final float radiusDp, depthDp, widthDp;

    Depth(int fill, int stroke, int edge, float radiusDp, float depthDp, float widthDp) {
      this.fill = fill;
      this.stroke = stroke;
      this.edge = edge;
      this.radiusDp = radiusDp;
      this.depthDp = depthDp;
      this.widthDp = widthDp;
    }

    @Override
    public void draw(Canvas canvas) {
      Rect b = getBounds();
      float r = dp(radiusDp), depth = dp(depthDp);
      if (depth > 0 && edge != ALPHA_NONE) {
        paint.setStyle(Paint.Style.FILL);
        paint.setColor(edge);
        rect.set(b.left, b.top + depth, b.right, b.bottom);
        canvas.drawRoundRect(rect, r, r, paint);
      }
      float bottom = b.bottom - depth;
      paint.setColor(fill);
      rect.set(b.left, b.top, b.right, bottom);
      canvas.drawRoundRect(rect, r, r, paint);
      if (stroke != ALPHA_NONE && widthDp > 0) {
        float strokeWidth = dp(widthDp) * 1.4f;
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(strokeWidth);
        paint.setColor(stroke);
        float inset = strokeWidth / 2f;
        rect.set(b.left + inset, b.top + inset, b.right - inset, bottom - inset);
        canvas.drawRoundRect(rect, r - inset, r - inset, paint);
      }
    }

    @Override
    public void setAlpha(int alpha) {
      paint.setAlpha(alpha);
    }

    @Override
    public void setColorFilter(ColorFilter filter) {
      paint.setColorFilter(filter);
    }

    @Override
    public int getOpacity() {
      return PixelFormat.TRANSLUCENT;
    }
  }
}
