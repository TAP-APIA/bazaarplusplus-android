package com.bazzarhelper.android.test;

import android.app.Instrumentation;
import android.os.Bundle;
import com.bazzarhelper.android.runtime.RuntimeConfig;
import com.bazzarhelper.android.runtime.RuntimeInstaller;

/**
 * Runs the real preparation step (unpack, extract game metadata, download the Unity proxy
 * dependency) and asserts the runtime then reports itself as ready. Slow and needs network, so it
 * is a separate runner.
 */
public final class PrepareTests extends Instrumentation {
  public void onCreate(Bundle b) {
    super.onCreate(b);
    start();
  }

  public void onStart() {
    try {
      long started = System.currentTimeMillis();
      RuntimeConfig config =
          RuntimeInstaller.prepare(
              getTargetContext(),
              message -> android.util.Log.i("PrepareTest", message));
      long elapsed = System.currentTimeMillis() - started;
      RuntimeInstaller.Preview preview = RuntimeInstaller.preview(getTargetContext());
      if (preview.needed())
        throw new AssertionError(
            "still needs work · unpack=" + preview.unpackRuntime + " game=" + preview.gameChanged
                + " download=" + preview.downloadUnity);
      if (config.unityVersion.isEmpty()) throw new AssertionError("unity version missing");
      Bundle out = new Bundle();
      out.putString(
          "stream",
          "PASS 3 prepare checks: unity " + config.unityVersion + ", ready in " + elapsed + "ms\n");
      finish(-1, out);
    } catch (Throwable e) {
      Bundle out = new Bundle();
      out.putString("stream", android.util.Log.getStackTraceString(e));
      finish(0, out);
    }
  }
}
