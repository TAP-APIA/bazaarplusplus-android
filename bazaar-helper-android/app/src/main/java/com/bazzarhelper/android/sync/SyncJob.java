package com.bazzarhelper.android.sync;

public final class SyncJob extends android.app.job.JobService {
  public boolean onStartJob(android.app.job.JobParameters p) {
    new Thread(
            () -> {
              boolean retry = false;
              try {
                com.bazzarhelper.android.HelperApplication.engine().sync();
              } catch (Exception e) {
                retry = true;
              }
              jobFinished(p, retry);
            },
            "outbox-job")
        .start();
    return true;
  }

  public boolean onStopJob(android.app.job.JobParameters p) {
    return true;
  }
}
