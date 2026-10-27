package com.bepinex.android.bpp;

import android.app.Application;

/**
 * The fused launcher has no application class of its own. Installing this one makes
 * {@code onCreate} run in every process, including {@code :game}, which is the process
 * that actually draws the grey Unity surface and runs Cpp2IL.
 */
public final class BppGameApplication extends Application {
    @Override
    public void onCreate() {
        super.onCreate();
        try {
            BppGameProcess.start(this);
        } catch (Throwable ignored) {
        }
    }
}
