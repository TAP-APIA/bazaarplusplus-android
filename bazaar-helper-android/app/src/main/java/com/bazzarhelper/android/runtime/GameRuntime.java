package com.bazzarhelper.android.runtime;

import android.app.*;
import android.content.*;
import android.content.pm.*;
import android.content.res.*;
import android.os.*;
import android.view.*;
import dalvik.system.PathClassLoader;
import java.io.*;
import java.lang.reflect.*;
import java.util.*;

/** Hosts only the installed game's activity under our UID, with an isolated native runtime. */
public final class GameRuntime {
  private static native void exempt();

  private static Application gameApplication;

  public static void install(Context helper) throws Exception {
    System.loadLibrary("helperjni");
    exempt();
    Context game =
        helper.createPackageContext(
            RuntimeInstaller.GAME, Context.CONTEXT_INCLUDE_CODE | Context.CONTEXT_IGNORE_SECURITY);
    ApplicationInfo info = game.getApplicationInfo();
    List<String> apks = new ArrayList<>();
    apks.add(info.sourceDir);
    if (info.splitSourceDirs != null) Collections.addAll(apks, info.splitSourceDirs);
    ClassLoader loader =
        new PathClassLoader(
            String.join(File.pathSeparator, apks), info.nativeLibraryDir, helper.getClassLoader()) {
          @Override
          protected Class<?> loadClass(String name, boolean resolve) throws ClassNotFoundException {
            synchronized (this) {
              Class<?> c = findLoadedClass(name);
              boolean parent =
                  name.startsWith("java.")
                      || name.startsWith("javax.")
                      || name.startsWith("android.")
                      || name.startsWith("dalvik.")
                      || name.startsWith("sun.")
                      || name.startsWith("org.w3c.")
                      || name.startsWith("org.xml.")
                      || name.startsWith("com.bazzarhelper.");
              if (c == null && !parent)
                try {
                  c = findClass(name);
                } catch (ClassNotFoundException ignored) {
                }
              if (c == null) c = super.loadClass(name, false);
              if (resolve) resolveClass(c);
              return c;
            }
          }

          @Override
          public String findLibrary(String name) {
            File file;
            if (name.equals("main") || name.equals("nextbep") || name.equals("helperjni"))
              file = new File(helper.getApplicationInfo().nativeLibraryDir, "lib" + name + ".so");
            else if (name.equals("il2cpp"))
              file = new File(helper.getDir("native-runtime", 0), "game/libil2cpp.so");
            else file = new File(info.nativeLibraryDir, "lib" + name + ".so");
            return file.isFile() ? file.getAbsolutePath() : super.findLibrary(name);
          }
        };
    Context gameLoader =
        new ContextWrapper(game) {
          @Override
          public ClassLoader getClassLoader() {
            return loader;
          }
        };
    String appClass = info.className;
    if (appClass == null) {
      GameApplication app = new GameApplication((Application) helper);
      gameApplication = app;
      app.bind(new GameContext(gameLoader, helper));
    } else {
      gameApplication =
          (Application) loader.loadClass(appClass).getDeclaredConstructor().newInstance();
      Method attach = Application.class.getDeclaredMethod("attach", Context.class);
      attach.setAccessible(true);
      attach.invoke(gameApplication, new GameContext(gameLoader, helper));
    }
    gameApplication.onCreate();
    // The helper UI writes the timer preferences in another process; pick them up on every resume.
    gameApplication.registerActivityLifecycleCallbacks(
        new Application.ActivityLifecycleCallbacks() {
          public void onActivityCreated(Activity a, Bundle state) {}

          public void onActivityStarted(Activity a) {}

          public void onActivityResumed(Activity a) {
            if (a.getIntent() != null && a.getIntent().hasExtra(RuntimeConfig.EXTRA))
              BattleTimerOverlay.reload();
          }

          public void onActivityPaused(Activity a) {}

          public void onActivityStopped(Activity a) {}

          public void onActivitySaveInstanceState(Activity a, Bundle state) {}

          public void onActivityDestroyed(Activity a) {}
        });
    Class<?> thread = Class.forName("android.app.ActivityThread");
    Object current = thread.getDeclaredMethod("currentActivityThread").invoke(null);
    Field f = thread.getDeclaredField("mInstrumentation");
    f.setAccessible(true);
    Instrumentation original = (Instrumentation) f.get(current);
    f.set(
        current,
        new Instrumentation() {
          private final Map<Activity, Intent> pending = new WeakHashMap<>();

          @Override
          public Activity newActivity(ClassLoader cl, String name, Intent intent)
              throws InstantiationException, IllegalAccessException, ClassNotFoundException {
            if (!name.equals(GameHostActivity.class.getName()))
              return original.newActivity(cl, name, intent);
            intent.setExtrasClassLoader(RuntimeConfig.class.getClassLoader());
            String target =
                helper
                    .getPackageManager()
                    .getLaunchIntentForPackage(RuntimeInstaller.GAME)
                    .getComponent()
                    .getClassName();
            Activity a = original.newActivity(loader, target, intent);
            pending.put(a, intent);
            return a;
          }

          @Override
          public void callActivityOnCreate(Activity a, Bundle state) {
            if (pending.remove(a) != null)
              try {
                Context host = a.getBaseContext();
                Field base = ContextWrapper.class.getDeclaredField("mBase");
                base.setAccessible(true);
                base.set(a, new GameContext(gameLoader, host));
                Field app = Activity.class.getDeclaredField("mApplication");
                app.setAccessible(true);
                app.set(a, gameApplication);
                Field res = ContextThemeWrapper.class.getDeclaredField("mResources");
                res.setAccessible(true);
                res.set(a, null);
                ActivityInfo ai =
                    helper
                        .getPackageManager()
                        .getActivityInfo(
                            new ComponentName(RuntimeInstaller.GAME, a.getClass().getName()), 0);
                Resources.Theme theme = a.getResources().newTheme();
                theme.applyStyle(
                    ai.getThemeResource() == 0
                        ? android.R.style.Theme_Black_NoTitleBar
                        : ai.getThemeResource(),
                    true);
                a.setTheme(theme);
                a.setRequestedOrientation(ai.screenOrientation);
                a.getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
              } catch (Exception e) {
                throw new IllegalStateException("无法准备游戏环境", e);
              }
            original.callActivityOnCreate(a, state);
            if (a.getIntent().hasExtra(RuntimeConfig.EXTRA)) BattleTimerOverlay.attach(a);
          }

          @Override
          public boolean onException(Object o, Throwable e) {
            return original.onException(o, e);
          }
        });
  }

  private static final class GameApplication extends Application {
    private final Application owner;

    GameApplication(Application owner) {
      this.owner = owner;
    }

    void bind(Context context) {
      attachBaseContext(context);
    }

    @Override
    public void registerActivityLifecycleCallbacks(ActivityLifecycleCallbacks callback) {
      owner.registerActivityLifecycleCallbacks(callback);
    }

    @Override
    public void unregisterActivityLifecycleCallbacks(ActivityLifecycleCallbacks callback) {
      owner.unregisterActivityLifecycleCallbacks(callback);
    }

    @Override
    public void registerComponentCallbacks(android.content.ComponentCallbacks callback) {
      owner.registerComponentCallbacks(callback);
    }

    @Override
    public void unregisterComponentCallbacks(android.content.ComponentCallbacks callback) {
      owner.unregisterComponentCallbacks(callback);
    }
  }

  private static final class GameContext extends ContextWrapper {
    private final Context host;
    private final ApplicationInfo info;

    GameContext(Context game, Context host) {
      super(game);
      this.host = host;
      info = new ApplicationInfo(game.getApplicationInfo());
      info.dataDir = host.getApplicationInfo().dataDir;
      info.nativeLibraryDir = "";
      info.uid = host.getApplicationInfo().uid;
    }

    @Override
    public ApplicationInfo getApplicationInfo() {
      return info;
    }

    @Override
    public String getOpPackageName() {
      return host.getOpPackageName();
    }

    @Override
    public Object getSystemService(String n) {
      return host.getSystemService(n);
    }

    @Override
    public Context getApplicationContext() {
      return gameApplication;
    }

    @Override
    public android.content.SharedPreferences getSharedPreferences(String n, int m) {
      return host.getSharedPreferences("game-" + n, m);
    }

    @Override
    public File getFilesDir() {
      return host.getFilesDir();
    }

    @Override
    public File getNoBackupFilesDir() {
      return host.getNoBackupFilesDir();
    }

    @Override
    public File getCacheDir() {
      return host.getCacheDir();
    }

    @Override
    public File getCodeCacheDir() {
      return host.getCodeCacheDir();
    }

    @Override
    public File getExternalFilesDir(String type) {
      return host.getExternalFilesDir(type);
    }

    @Override
    public File getExternalCacheDir() {
      return host.getExternalCacheDir();
    }

    @Override
    public Display getDisplay() {
      return host.getDisplay();
    }
  }
}
