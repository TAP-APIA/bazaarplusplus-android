package com.bazzarhelper.android.sync;

import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import org.json.JSONObject;

public final class HttpApi implements SyncEngine.Transport {
  public static final String ENDPOINT = "https://4ork.cn/bazzar/api/v1";

  public static final class HttpFailure extends IOException {
    public final int status;

    public HttpFailure(int status) {
      super("HTTP " + status);
      this.status = status;
    }
  }

  @Override
  public JSONObject request(String method, String path, String token, JSONObject body)
      throws Exception {
    HttpURLConnection c = (HttpURLConnection) new URL(ENDPOINT + path).openConnection();
    c.setConnectTimeout(15000);
    c.setReadTimeout(20000);
    c.setRequestMethod(method);
    c.setInstanceFollowRedirects(false);
    c.setRequestProperty("Accept", "application/json");
    if (token != null && !token.isEmpty()) c.setRequestProperty("Authorization", "Bearer " + token);
    try {
      if (body != null) {
        c.setDoOutput(true);
        c.setRequestProperty("Content-Type", "application/json; charset=utf-8");
        try (OutputStream out = c.getOutputStream()) {
          out.write(body.toString().getBytes(StandardCharsets.UTF_8));
        }
      }
      int status = c.getResponseCode();
      if (status < 200 || status >= 300) throw new HttpFailure(status);
      ByteArrayOutputStream out = new ByteArrayOutputStream();
      try (InputStream in = c.getInputStream()) {
        byte[] b = new byte[8192];
        int n;
        while ((n = in.read(b)) != -1) {
          out.write(b, 0, n);
          if (out.size() > 4 * 1024 * 1024) throw new IOException("Response too large");
        }
      }
      String text = new String(out.toByteArray(), StandardCharsets.UTF_8);
      return text.isEmpty() ? new JSONObject() : new JSONObject(text);
    } finally {
      c.disconnect();
    }
  }
}
