package com.bazzarhelper.android.runtime;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;

final class Streams {
  static byte[] read(InputStream in, int limit) throws IOException {
    ByteArrayOutputStream out = new ByteArrayOutputStream();
    byte[] b = new byte[8192];
    int n;
    while ((n = in.read(b)) != -1) {
      if (out.size() + n > limit) throw new IOException("文件过大");
      out.write(b, 0, n);
    }
    return out.toByteArray();
  }

  static void transfer(InputStream in, OutputStream out) throws IOException {
    byte[] b = new byte[65536];
    int n;
    while ((n = in.read(b)) != -1) out.write(b, 0, n);
  }

  static String text(File f) throws IOException {
    return new String(Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8);
  }

  static void text(File f, String text) throws IOException {
    Files.write(f.toPath(), text.getBytes(StandardCharsets.UTF_8));
  }
}
