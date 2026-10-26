package com.bepinex.android.bpp;

import android.app.Activity;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.window.OnBackInvokedCallback;
import android.window.OnBackInvokedDispatcher;

/** Routes system Back to the overlay owner, without changing Back outside BPP panels. */
public final class BppBackBridge {
    private static final Handler MAIN = new Handler(Looper.getMainLooper());
    private static volatile boolean consume;
    private static boolean pending;
    private static Activity activity;
    private static OnBackInvokedCallback callback;
    private static volatile boolean registered;
    private static boolean scheduled;

    private BppBackBridge() {}

    public static void install(final Activity value) {
        if (Build.VERSION.SDK_INT < 33 || value == null) return;
        MAIN.post(new Runnable() {
            @Override public void run() {
                if (activity != value) {
                    unregister();
                    activity = value;
                }
                if (callback == null) {
                    callback = new OnBackInvokedCallback() {
                        @Override public void onBackInvoked() {
                            recordBack();
                        }
                    };
                }
                reconcile();
            }
        });
    }

    public static synchronized void setConsume(boolean value) {
        consume = value;
        if (!value) pending = false;
        if (registered == value || scheduled || Build.VERSION.SDK_INT < 33) return;
        scheduled = true;
        MAIN.post(new Runnable() {
            @Override public void run() {
                synchronized (BppBackBridge.class) { scheduled = false; }
                reconcile();
            }
        });
    }

    private static synchronized void recordBack() {
        if (consume) {
            pending = true;
            Log.i("BppBackBridge", "Back consumed");
        }
    }

    public static synchronized boolean takeBackPress() {
        boolean value = pending;
        pending = false;
        return value;
    }

    public static void uninstall() {
        synchronized (BppBackBridge.class) { consume = false; pending = false; }
        MAIN.post(new Runnable() {
            @Override public void run() {
                unregister();
                activity = null;
                callback = null;
            }
        });
    }

    private static void reconcile() {
        if (activity == null || callback == null || Build.VERSION.SDK_INT < 33) return;
        try {
            if (consume && !registered) {
                activity.getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                        OnBackInvokedDispatcher.PRIORITY_DEFAULT, callback);
                registered = true;
                Log.i("BppBackBridge", "Callback registered on Android UI thread");
            } else if (!consume && registered) {
                unregister();
            }
        } catch (RuntimeException error) {
            Log.e("BppBackBridge", "Callback reconciliation failed", error);
        }
    }

    private static void unregister() {
        if (registered && activity != null && callback != null) {
            try {
                activity.getOnBackInvokedDispatcher().unregisterOnBackInvokedCallback(callback);
            } catch (RuntimeException error) {
                Log.w("BppBackBridge", "Activity is releasing its callback", error);
            }
        }
        registered = false;
    }
}
