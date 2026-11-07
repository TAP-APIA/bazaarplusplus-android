package com.bazzarhelper.android;

import android.app.*;
import android.app.job.*;
import android.content.*;
import com.bazzarhelper.android.runtime.*;
import com.bazzarhelper.android.sync.*;
import java.util.concurrent.*;

public final class HelperApplication extends Application {
  private static SyncEngine engine;
  private static final ScheduledExecutorService worker =
      Executors.newSingleThreadScheduledExecutor();

  public static SyncEngine engine() {
    return engine;
  }

  public static void wake() {
    worker.execute(HelperApplication::sync);
  }

  private static void sync() {
    try {
      engine.sync();
    } catch (Exception e) {
      engine.db.put(
          "syncStatus",
          e instanceof HttpApi.HttpFailure
              ? "同步暂未完成（HTTP " + ((HttpApi.HttpFailure) e).status + "），将自动重试"
              : "网络暂不可用，将自动重试");
    }
  }

  @Override
  protected void attachBaseContext(Context base) {
    super.attachBaseContext(base);
    engine = new SyncEngine(this);
  }

  @Override
  public void onCreate() {
    super.onCreate();
    Diag.init(this);
    if (Application.getProcessName().endsWith(":game"))
      try {
        GameRuntime.install(this);
      } catch (Exception e) {
        android.util.Log.e("BazaarHelper", "游戏运行环境初始化失败", e);
        throw new IllegalStateException("游戏运行环境初始化失败", e);
      }
    worker.scheduleWithFixedDelay(HelperApplication::sync, 3, 20, TimeUnit.SECONDS);
    if (!Application.getProcessName().endsWith(":game")) {
      JobScheduler jobs = getSystemService(JobScheduler.class);
      jobs.schedule(
          new JobInfo.Builder(104, new ComponentName(this, SyncJob.class))
              .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
              .setPeriodic(15 * 60 * 1000L)
              .build());
    }
  }
}
