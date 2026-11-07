package com.bazzarhelper.android.test;

import android.app.*;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import java.io.*;
import java.lang.reflect.*;
import java.util.zip.*;

/**
 * Verifies the support bundle end to end: a real Activity builds the zip, MediaStore accepts it,
 * and reading it back yields report.txt plus the log entries. Removes its own row afterwards.
 */
public final class ExportTests extends Instrumentation {
  public void onCreate(Bundle b) {
    super.onCreate(b);
    start();
  }

  public void onStart() {
    Uri uri = null;
    try {
      Activity a =
          startActivitySync(
              new Intent()
                  .setClassName(
                      "com.bazzarhelper.android", "com.bazzarhelper.android.ui.MainActivity")
                  .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
      waitForIdleSync();

      Class<?> type = Class.forName("com.bazzarhelper.android.ui.LogExport");
      Method write = type.getDeclaredMethod("write", Activity.class);
      write.setAccessible(true);
      Object result = write.invoke(null, a);
      if (result == null) throw new AssertionError("export returned nothing");

      Field nameField = result.getClass().getDeclaredField("name");
      nameField.setAccessible(true);
      String name = (String) nameField.get(result);
      Field uriField = result.getClass().getDeclaredField("uri");
      uriField.setAccessible(true);
      uri = (Uri) uriField.get(result);
      Field bytesField = result.getClass().getDeclaredField("bytes");
      bytesField.setAccessible(true);
      int bytes = (Integer) bytesField.get(result);

      if (!name.endsWith(".zip")) throw new AssertionError("not a zip: " + name);
      if (bytes < 200) throw new AssertionError("suspiciously small: " + bytes);
      if (uri == null) throw new AssertionError("no content uri");

      int entries = 0;
      boolean hasReport = false, hasAppLog = false;
      try (InputStream in = a.getContentResolver().openInputStream(uri);
          ZipInputStream zip = new ZipInputStream(in)) {
        ZipEntry entry;
        while ((entry = zip.getNextEntry()) != null) {
          entries++;
          if (entry.getName().equals("report.txt")) {
            ByteArrayOutputStream text = new ByteArrayOutputStream();
            byte[] buffer = new byte[4096];
            int read;
            while ((read = zip.read(buffer)) > 0) text.write(buffer, 0, read);
            String report = text.toString("UTF-8");
            if (!report.contains("大巴扎助手")) throw new AssertionError("report missing header");
            if (!report.contains("应用:")) throw new AssertionError("report missing app line");
            hasReport = true;
          }
          if (entry.getName().equals("logs/app.log")) hasAppLog = true;
        }
      }
      if (!hasReport) throw new AssertionError("report.txt missing");
      if (!hasAppLog) throw new AssertionError("logs/app.log missing");
      if (entries < 2) throw new AssertionError("entries " + entries);

      Bundle out = new Bundle();
      out.putString(
          "stream",
          "PASS 7 export checks: " + name + " " + bytes + " bytes, " + entries + " entries\n");
      finish(-1, out);
    } catch (Throwable e) {
      Bundle out = new Bundle();
      out.putString("stream", android.util.Log.getStackTraceString(e));
      finish(0, out);
    } finally {
      if (uri != null)
        try {
          getTargetContext().getContentResolver().delete(uri, null, null);
        } catch (Exception ignored) {
        }
    }
  }
}
