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
 * Holds :game at foreground-service importance while the first-run generator is alive.
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
        BppGameProcess.logEvent(this, "FGS service onCreate process="
            + Application.getProcessName() + " pid=" + android.os.Process.myPid());
        promote();
        if (!isGameProcess()) {
            BppGameProcess.logEvent(this, "FGS service release reason=not :game process="
                + Application.getProcessName());
            release();
        }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        boolean stop = !isGameProcess() || (intent != null && ACTION_STOP.equals(intent.getAction()));
        BppGameProcess.logEvent(this, "FGS service onStartCommand stop=" + stop
            + " action=" + (intent == null ? "null" : intent.getAction())
            + " startId=" + startId);
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
        BppGameProcess.logEvent(this, "FGS service onDestroy released=" + released);
        try {
            stopForeground(STOP_FOREGROUND_REMOVE);
        } catch (Throwable t) {
            BppGameProcess.logEvent(this, "FGS service onDestroy stopForeground failed", t);
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
                startForeground(
                    NOTIFICATION_ID,
                    notification,
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
                );
                BppGameProcess.logEvent(this, "FGS service startForeground ok type=specialUse id="
                    + NOTIFICATION_ID);
            } else {
                startForeground(NOTIFICATION_ID, notification);
                BppGameProcess.logEvent(this, "FGS service startForeground ok id="
                    + NOTIFICATION_ID);
            }
        } catch (Throwable t) {
            BppGameProcess.logEvent(this, "FGS service startForeground exception id="
                + NOTIFICATION_ID, t);
        }
    }

    private void release() {
        if (released) return;
        released = true;
        BppGameProcess.logEvent(this, "FGS service release begin pid=" + android.os.Process.myPid());
        try {
            stopForeground(STOP_FOREGROUND_REMOVE);
        } catch (Throwable t) {
            BppGameProcess.logEvent(this, "FGS service release stopForeground failed", t);
        }
        try {
            stopSelf();
        } catch (Throwable t) {
            BppGameProcess.logEvent(this, "FGS service release stopSelf failed", t);
        }
        BppGameProcess.logEvent(this, "FGS service release end pid=" + android.os.Process.myPid());
    }

    private static boolean isGameProcess() {
        String process = Application.getProcessName();
        return process != null && process.endsWith(":game");
    }
}
