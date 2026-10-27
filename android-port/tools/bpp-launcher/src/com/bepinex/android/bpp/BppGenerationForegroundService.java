package com.bepinex.android.bpp;

import android.app.Application;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.Build;
import android.os.IBinder;

/**
 * Holds {@code :game} at foreground-service importance while the first-run generator is alive.
 * A paused activity drops to cached and the low-memory killer reclaims the multi-gigabyte process.
 */
public final class BppGenerationForegroundService extends Service {
    static final String ACTION_STOP = "com.bepinex.android.bpp.action.STOP_GENERATION_FOREGROUND";
    private static final String CHANNEL_ID = "bpp-generation";
    private static final int NOTIFICATION_ID = 41;
    private boolean released;

    @Override
    public void onCreate() {
        super.onCreate();
        promote();
        if (!isGameProcess()) release();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        boolean stop = !isGameProcess() || (intent != null && ACTION_STOP.equals(intent.getAction()));
        if (stop) {
            if (!released) promote();
            release();
            return START_NOT_STICKY;
        }
        promote();
        return START_NOT_STICKY;
    }

    @Override
    public void onDestroy() {
        try {
            stopForeground(STOP_FOREGROUND_REMOVE);
        } catch (Throwable ignored) {
        }
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    private void promote() {
        try {
            NotificationManager manager = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            if (manager != null && Build.VERSION.SDK_INT >= 26) {
                NotificationChannel channel = new NotificationChannel(
                    CHANNEL_ID,
                    "模组生成",
                    NotificationManager.IMPORTANCE_LOW
                );
                channel.setDescription("首次启动生成模组文件时保持游戏进程在前台");
                manager.createNotificationChannel(channel);
            }
            Notification notification = new Notification.Builder(this, CHANNEL_ID)
                .setContentTitle("正在生成模组文件")
                .setContentText("请回到游戏画面，不要切走")
                .setSmallIcon(android.R.drawable.stat_notify_sync)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setCategory(Notification.CATEGORY_PROGRESS)
                .build();
            if (Build.VERSION.SDK_INT >= 34) {
                startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE);
            } else {
                startForeground(NOTIFICATION_ID, notification);
            }
        } catch (Throwable ignored) {
        }
    }

    private void release() {
        if (released) return;
        released = true;
        try {
            stopForeground(STOP_FOREGROUND_REMOVE);
        } catch (Throwable ignored) {
        }
        try {
            stopSelf();
        } catch (Throwable ignored) {
        }
    }

    private static boolean isGameProcess() {
        String process = Application.getProcessName();
        return process != null && process.endsWith(":game");
    }
}
