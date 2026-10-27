package com.bepinex.android.bpp;

import android.app.Application;
import android.content.Context;

/**
 * The fused launcher has no application class of its own. Installing this one makes
 * attachBaseContext/onCreate run in every process, including :game, which is the process
 * that actually draws the grey Unity surface and runs Cpp2IL.
 */
public final class BppGameApplication extends Application {

    @Override
    protected void attachBaseContext(Context base) {
        super.attachBaseContext(base);
        try {
            BppGameProcess.start(base, "BppGameApplication.attachBaseContext");
        } catch (Throwable t) {
            BppGameProcess.logEvent(base, "BppGameApplication.attachBaseContext start failed", t);
        }
    }

    @Override
    public void onCreate() {
        super.onCreate();
        try {
            BppGameProcess.start(this, "BppGameApplication.onCreate");
        } catch (Throwable t) {
            BppGameProcess.logEvent(this, "BppGameApplication.onCreate start failed", t);
        }
    }
}
