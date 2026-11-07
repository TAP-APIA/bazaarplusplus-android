package com.bazzarhelper.android.runtime;

import android.app.Activity;
import android.view.*;
import android.widget.*;
import java.util.*;

public final class RuntimeCallbacks {
  private static final Map<Activity, TextView> views = new WeakHashMap<>();

  public static void SetLoadingState(Activity a, boolean visible) {
    a.runOnUiThread(
        () -> {
          TextView v = views.get(a);
          if (visible && v == null) {
            v = new TextView(a);
            v.setTextColor(0xffffffff);
            v.setBackgroundColor(0xee182231);
            v.setPadding(32, 24, 32, 24);
            v.setText("正在初始化战绩采集，首次启动需要生成游戏代理…");
            ((ViewGroup) a.getWindow().getDecorView())
                .addView(v, new ViewGroup.LayoutParams(-1, -2));
            views.put(a, v);
          }
          if (v != null) v.setVisibility(visible ? View.VISIBLE : View.GONE);
        });
  }

  public static void SetLoadingText(Activity a, String message) {
    a.runOnUiThread(
        () -> {
          TextView v = views.get(a);
          if (v != null) v.setText(message);
        });
  }
}
