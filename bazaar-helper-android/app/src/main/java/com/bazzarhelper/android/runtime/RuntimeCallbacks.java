package com.bazzarhelper.android.runtime;

import android.app.Activity;

/**
 * Entry points the pinned launcher calls through JNI. Method names and signatures are fixed by
 * {@code foundation/cpp/nextbep/src/utilities/java.cpp} ({@code SetLoadingState(Activity, Z)} and
 * {@code SetLoadingText(Activity, String)}), so the visuals live in {@link LaunchOverlay}.
 */
public final class RuntimeCallbacks {
  private RuntimeCallbacks() {}

  public static void SetLoadingState(Activity activity, boolean visible) {
    if (visible) LaunchOverlay.show(activity);
    else LaunchOverlay.hide(activity);
  }

  public static void SetLoadingText(Activity activity, String message) {
    LaunchOverlay.text(activity, message);
  }
}
