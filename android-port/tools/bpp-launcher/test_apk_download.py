"""Execute the production download lifecycle against stateful Android service fakes."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

STUBS = {
    "android/net/Uri.java": """package android.net; public class Uri {
        public String value; private Uri(String s){value=s;} public static Uri parse(String s){return new Uri(s);}
        public String getScheme(){return value.split(":")[0];}
    }""",
    "android/os/Environment.java": 'package android.os; public class Environment { public static final String DIRECTORY_DOWNLOADS="Download"; }',
    "android/content/SharedPreferences.java": """package android.content; import java.util.*;
    public class SharedPreferences {
        Map<String,Object> values=new HashMap<>(); public boolean writable=true;
        public long getLong(String key,long fallback){return (Long)values.getOrDefault(key,fallback);}
        public String getString(String key,String fallback){return (String)values.getOrDefault(key,fallback);}
        public Editor edit(){return new Editor();}
        public class Editor {
            Map<String,Object> pending=new HashMap<>(values);
            public Editor putLong(String k,long v){pending.put(k,v);return this;}
            public Editor putString(String k,String v){pending.put(k,v);return this;}
            public Editor clear(){pending.clear();return this;}
            public boolean commit(){if(!writable)return false;values=pending;return true;}
            public void apply(){commit();}
        }
    }""",
    "android/content/pm/PackageInfo.java": """package android.content.pm; public class PackageInfo {
        public String packageName="com.bepinex.android",versionName="6.0.0-preview.44";
        public long versionCode=44; public long getLongVersionCode(){return versionCode;}
    }""",
    "android/content/pm/PackageManager.java": """package android.content.pm; public class PackageManager {
        public PackageInfo archive=new PackageInfo(),installed=new PackageInfo();
        public PackageManager(){installed.versionCode=43;}
        public PackageInfo getPackageArchiveInfo(String path,int flags){return archive;}
        public PackageInfo getPackageInfo(String name,int flags){return installed;}
    }""",
    "android/database/Cursor.java": """package android.database; public class Cursor implements AutoCloseable {
        public boolean exists=true,closed; public long[] values={2,0,50,100};
        public boolean moveToFirst(){return exists;}
        public int getColumnIndexOrThrow(String key){return Integer.parseInt(key);}
        public int getInt(int i){return (int)values[i];} public long getLong(int i){return values[i];}
        public void close(){closed=true;}
    }""",
    "android/app/DownloadManager.java": """package android.app; import android.net.Uri;
    public class DownloadManager {
        public static final int STATUS_FAILED=16,STATUS_SUCCESSFUL=8,STATUS_PAUSED=4;
        public static final String COLUMN_STATUS="0",COLUMN_REASON="1",COLUMN_BYTES_DOWNLOADED_SO_FAR="2",COLUMN_TOTAL_SIZE_BYTES="3";
        public android.database.Cursor cursor=new android.database.Cursor();
        public int enqueued,removed; public Uri uri=Uri.parse("content://downloads/7"); public Request request;
        public static class Query {public Query setFilterById(long... ids){return this;}}
        public static class Request {
            public static final int VISIBILITY_VISIBLE_NOTIFY_COMPLETED=1;
            public Uri uri; public String file;
            public Request(Uri u){uri=u;} public Request setTitle(String s){return this;}
            public Request setDescription(String s){return this;} public Request setMimeType(String s){return this;}
            public Request setNotificationVisibility(int n){return this;}
            public Request setDestinationInExternalFilesDir(android.content.Context c,String dir,String name){file=name;return this;}
        }
        public long enqueue(Request r){request=r;enqueued++;return 7;}
        public int remove(long... ids){removed++;return 1;}
        public android.database.Cursor query(Query q){return cursor;}
        public Uri getUriForDownloadedFile(long id){return uri;}
    }""",
    "android/content/Context.java": """package android.content; public class Context {
        public static final String DOWNLOAD_SERVICE="download"; public static final int MODE_PRIVATE=0;
        public final android.app.DownloadManager manager=new android.app.DownloadManager();
        public final SharedPreferences preferences=new SharedPreferences();
        public final android.content.pm.PackageManager packages=new android.content.pm.PackageManager();
        public java.io.File directory;
        public Object getSystemService(String name){return manager;}
        public SharedPreferences getSharedPreferences(String name,int mode){return preferences;}
        public java.io.File getExternalFilesDir(String type){return directory;}
        public android.content.pm.PackageManager getPackageManager(){return packages;}
        public String getPackageName(){return "com.bepinex.android";}
    }""",
    "com/bepinex/android/bpp/BppLauncherUpdateChecker.java": """package com.bepinex.android.bpp;
    public class BppLauncherUpdateChecker {
        public static class Update {public String version="6.0.0-preview.44",apkUrl="https://github.com/test.apk";public long apkSize=100;}
        public static int compareVersions(String a,String b){return a.compareTo(b);}
    }""",
    "DownloadTests.java": """import com.bepinex.android.bpp.*; import android.content.Context; import java.nio.file.*;
    public class DownloadTests {
        interface Work {void run() throws Exception;}
        static int assertions;
        static void check(boolean b){assertions++;if(!b)throw new AssertionError("Assertion "+assertions);}
        static void rejects(Work work) throws Exception {try{work.run();throw new AssertionError("Expected rejection");}catch(java.io.IOException expected){assertions++;}}
        public static void main(String[] args) throws Exception {
            Context c=new Context(); c.directory=new java.io.File(args[0]);
            BppApkDownload d=new BppApkDownload(c); var update=new BppLauncherUpdateChecker.Update();
            check(!d.hasDownload()); d.start(update); check(d.hasDownload());
            check(c.manager.request.uri.value.equals(update.apkUrl));
            d.start(update); check(c.manager.enqueued==1);
            d=new BppApkDownload(c); check(d.hasDownload());
            check(d.state().message.contains("50%") && !d.state().complete); check(c.manager.cursor.closed);
            c.manager.cursor.values[0]=4; check(d.state().message.contains("暂停"));
            BppApkDownload restored=d; rejects(()->restored.installationUri());
            c.manager.cursor.values[0]=16; rejects(()->restored.state());
            c.manager.cursor.exists=false; rejects(()->restored.state()); c.manager.cursor.exists=true;
            c.manager.cursor.values[0]=8; check(d.state().complete);
            rejects(()->restored.installationUri());
            Path apk=c.directory.toPath().resolve("bpp-update.apk"); Files.write(apk,new byte[99]);
            rejects(()->restored.installationUri()); Files.write(apk,new byte[100]);
            check(d.installationUri().getScheme().equals("content"));
            c.packages.archive.packageName="unrelated"; rejects(()->restored.installationUri());
            c.packages.archive.packageName=c.getPackageName(); c.packages.archive.versionName="6.0.0-preview.45";
            rejects(()->restored.installationUri()); c.packages.archive.versionName=update.version;
            c.packages.archive.versionCode=43; rejects(()->restored.installationUri()); c.packages.archive.versionCode=44;
            c.manager.uri=android.net.Uri.parse("file:///tmp/update.apk"); rejects(()->restored.installationUri());
            check(!d.alreadyInstalled("6.0.0-preview.43")); check(d.alreadyInstalled(update.version));
            d.clear(); check(!d.hasDownload() && c.manager.removed==1);
            d.start(update); check(!Files.exists(apk)); check(c.manager.enqueued==2); d.clear();
            c.preferences.writable=false; rejects(()->restored.start(update)); check(c.manager.removed==3);
            c.preferences.writable=true; c.directory=null; rejects(()->restored.start(update));
            System.out.println("APK download tests passed: "+assertions+" assertions");
        }
    }""",
}


def run():
    java_home = os.environ.get("JAVA_HOME")
    suffix = ".exe" if os.name == "nt" else ""
    javac = str(Path(java_home) / "bin" / ("javac" + suffix)) if java_home else shutil.which("javac")
    java = str(Path(java_home) / "bin" / ("java" + suffix)) if java_home else shutil.which("java")
    if not javac or not java:
        raise RuntimeError("JDK 17+ required")
    with tempfile.TemporaryDirectory(prefix="bpp-download-tests-") as temporary:
        root = Path(temporary)
        sources = []
        for relative, code in STUBS.items():
            target = root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(code, encoding="utf-8")
            sources.append(str(target))
        production = Path(__file__).parent / "src/com/bepinex/android/bpp/BppApkDownload.java"
        subprocess.run([javac, "-encoding", "UTF-8", "--release", "11", "-d", str(root), *sources, str(production)], check=True)
        subprocess.run([java, "-cp", str(root), "DownloadTests", str(root)], check=True)


if __name__ == "__main__":
    run()
