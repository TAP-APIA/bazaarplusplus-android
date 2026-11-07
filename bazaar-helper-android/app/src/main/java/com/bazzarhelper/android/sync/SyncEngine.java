package com.bazzarhelper.android.sync;

import android.content.Context;
import android.database.Cursor;
import com.bazzarhelper.android.data.RecordDatabase;
import java.io.*;
import java.nio.channels.*;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.time.Instant;
import java.util.*;
import org.json.*;

/** Durable, account-scoped outbox. Networking never runs inside a database transaction. */
public final class SyncEngine {
  public interface Transport {
    JSONObject request(String method, String path, String token, JSONObject body) throws Exception;
  }

  public final RecordDatabase db;
  private final Transport api;

  public SyncEngine(Context c) {
    this(new RecordDatabase(c), new HttpApi());
  }

  public SyncEngine(RecordDatabase db, Transport api) {
    this.db = db;
    this.api = api;
  }

  public JSONObject login() throws Exception {
    JSONObject s =
        db.transaction(
            () -> {
              JSONObject previous = db.session();
              if (!previous.optString("token").isEmpty())
                throw new IllegalStateException("请先退出当前登录");
              String client = db.get("clientUuid", UUID.randomUUID().toString());
              db.put("clientUuid", client);
              JSONObject next =
                  new JSONObject()
                      .put("generation", UUID.randomUUID().toString())
                      .put("token", UUID.randomUUID() + "-" + UUID.randomUUID())
                      .put("clientUuid", client);
              db.session(next);
              return next;
            });
    JSONObject qr =
        api.request(
            "POST",
            "/binding-sessions",
            "",
            new JSONObject()
                .put("protocolVersion", 3)
                .put("clientUuid", s.getString("clientUuid"))
                .put("deviceToken", s.getString("token"))
                .put("displayName", "大巴扎助手 Android")
                .put("platform", "android"));
    db.transaction(
        () -> {
          if (db.isCurrent(s)) {
            s.put("nonce", qr.getString("nonce")).put("expiresAt", qr.getString("expiresAt"));
            db.session(s);
          }
          return null;
        });
    return qr;
  }

  public boolean poll() throws Exception {
    JSONObject s = db.session();
    if (s.optString("nonce").isEmpty()) return false;
    JSONObject p = api.request("GET", "/binding-sessions/" + s.getString("nonce"), "", null);
    if (!"bound".equals(p.optString("status"))) return false;
    JSONObject me = api.request("GET", "/device", s.getString("token"), null);
    return db.transaction(
        () -> {
          if (!db.isCurrent(s)) return false;
          String owner = me.getString("accountId");
          if (owner.isEmpty()) throw new IOException("无效账号");
          s.put("owner", owner)
              .put("alias", me.optString("accountAlias", "微信账号"))
              .put("checkedAt", System.currentTimeMillis())
              .put("rotatedAt", System.currentTimeMillis());
          s.remove("nonce");
          db.session(s);
          db.getWritableDatabase()
              .execSQL(
                  "INSERT OR REPLACE INTO sessions(stamp,owner) VALUES(?,?)",
                  new Object[] {s.getString("generation"), owner});
          return true;
        });
  }

  /** Drops an unclaimed binding session so a fresh code / link can be requested. */
  public void abandonLogin() throws Exception {
    db.transaction(
        () -> {
          JSONObject s = db.session();
          if (!s.optString("owner").isEmpty()) throw new IllegalStateException("请先退出当前登录");
          db.session(new JSONObject());
          return null;
        });
  }

  public void logout() throws Exception {
    db.transaction(
        () -> {
          JSONObject s = db.session();
          com.bazzarhelper.android.data.CredentialCipher cipher =
              new com.bazzarhelper.android.data.CredentialCipher(
                  "com.bazzarhelper.android.revokes");
          String saved = db.get("revokes", "");
          JSONArray revokes = new JSONArray(saved.isEmpty() ? "[]" : cipher.decrypt(saved));
          if (!s.optString("token").isEmpty()) revokes.put(s.getString("token"));
          if (!s.optString("nextToken").isEmpty()) revokes.put(s.getString("nextToken"));
          db.put(
              "revokes",
              new com.bazzarhelper.android.data.CredentialCipher("com.bazzarhelper.android.revokes")
                  .encrypt(revokes.toString()));
          db.session(new JSONObject());
          return null;
        });
  }

  public String stamp() throws Exception {
    JSONObject s = db.session();
    return s.optString("owner").isEmpty() ? "" : s.optString("generation");
  }

  public void beginRun(JSONObject data) throws Exception {
    String id = data.getString("runId");
    validId(id);
    db.getWritableDatabase()
        .execSQL(
            "INSERT INTO run_facts(id,started,mode,facts) VALUES(?,?,?,?) ON CONFLICT(id) DO UPDATE"
                + " SET mode=COALESCE(excluded.mode,mode),facts=COALESCE(excluded.facts,facts)",
            new Object[] {
              id,
              Instant.now().toString(),
              data.optString("gameMode", null),
              data.has("victories") ? select(data, "victories losses finalDay").toString() : null
            });
  }

  public void completeRun(String id) {
    db.getWritableDatabase()
        .execSQL(
            "UPDATE run_facts SET ended=COALESCE(ended,?) WHERE id=?",
            new Object[] {Instant.now().toString(), id});
  }

  public void capture(String stamp, JSONObject envelope) throws Exception {
    String run = envelope.getString("runId");
    validId(run);
    JSONObject detail = envelope.getJSONObject("detail"), battle = detail.getJSONObject("battle");
    String id = battle.getString("battleId");
    validId(id);
    if (!Arrays.asList("win", "loss").contains(battle.getString("result")))
      throw new IOException("无效战果");
    if (envelope.toString().length() > 1024 * 1024) throw new IOException("记录过大");
    // Only protocol fields enter the durable record, never arbitrary game objects.
    JSONObject clean =
        new JSONObject()
            .put("runId", run)
            .put(
                "detail",
                new JSONObject()
                    .put(
                        "battle",
                        select(
                            battle,
                            "battleId recordedAt day hour playerHero opponentHero result durationMs"
                                + " captureStatus"))
                    .put(
                        "cards",
                        cleanArray(
                            detail.optJSONArray("cards"),
                            100,
                            "side zone templateId instanceId name tier size enchant tagsJson"
                                + " attributesJson boardSection socket position"))
                    .put(
                        "metrics",
                        cleanArray(
                            detail.optJSONArray("metrics"),
                            10000,
                            "side sourceInstanceId sourceKind metric value unit eventCount"
                                + " coverage")));
    for (String k : new String[] {"gameDataEtag", "dataVersion", "dataVersionSource"})
      if (envelope.has(k)) clean.put(k, envelope.get(k));
    db.transaction(
        () -> {
          String owner = stamp.isEmpty() ? "" : db.owner(stamp);
          if (owner == null) throw new IOException("无效归属");
          db.getWritableDatabase()
              .execSQL(
                  "INSERT OR IGNORE INTO run_facts(id,started) VALUES(?,?)",
                  new Object[] {run, battle.getString("recordedAt")});
          // Same account + stable battle ID is idempotent; enriched captures replace earlier
          // partial data.
          db.getWritableDatabase()
              .execSQL(
                  "INSERT OR REPLACE INTO battles(owner,id,run_id,recorded,payload)"
                      + " VALUES(?,?,?,?,?)",
                  new Object[] {owner, id, run, battle.getString("recordedAt"), clean.toString()});
          return null;
        });
  }

  public void sync() throws Exception {
    try (RandomAccessFile file = new RandomAccessFile(db.uploadLock(), "rw");
        FileChannel ch = file.getChannel()) {
      FileLock lock;
      try {
        lock = ch.tryLock();
      } catch (OverlappingFileLockException busy) {
        return;
      }
      if (lock == null) return;
      JSONObject activeSession = null;
      try {
        revokePending();
        JSONObject s = db.session();
        activeSession = s;
        String owner = s.optString("owner");
        if (owner.isEmpty()) return;
        if (System.currentTimeMillis() - s.optLong("checkedAt") > 86400000L) {
          JSONObject me = api.request("GET", "/device", s.getString("token"), null);
          if (!owner.equals(me.optString("accountId"))) throw new IOException("账号不匹配");
          db.transaction(
              () -> {
                if (db.isCurrent(s)) {
                  s.put("checkedAt", System.currentTimeMillis());
                  db.session(s);
                }
                return null;
              });
        }
        rotateIfDue(s);
        for (String run : db.runIds(owner)) {
          if (!db.isCurrent(s)) return;
          List<JSONObject> battles = db.records(owner, run);
          JSONObject summary = summarize(run, battles);
          upload(s, "run", run, summary);
          for (JSONObject b : battles) {
            if (!db.isCurrent(s)) return;
            upload(
                s,
                "battle",
                b.getJSONObject("detail").getJSONObject("battle").getString("battleId"),
                b);
          }
        }
        db.put("syncStatus", "同步完成 · " + Instant.now().toString());
      } catch (HttpApi.HttpFailure failure) {
        if (failure.status == 401 && activeSession != null) {
          JSONObject expected = activeSession;
          db.transaction(
              () -> {
                if (db.isCurrent(expected)) {
                  db.session(new JSONObject());
                  db.put("syncStatus", "登录已失效，请重新扫码");
                }
                return null;
              });
        }
        throw failure;
      } finally {
        lock.release();
      }
    }
  }

  private void rotateIfDue(JSONObject s) throws Exception {
    String candidate = s.optString("nextToken");
    long now = System.currentTimeMillis();
    if (candidate.isEmpty()
        && now - s.optLong("rotatedAt", s.optLong("checkedAt", now)) < 7 * 86400000L) return;
    if (candidate.isEmpty()) {
      candidate = UUID.randomUUID() + "-" + UUID.randomUUID();
      final String pending = candidate;
      boolean saved =
          db.transaction(
              () -> {
                if (!db.isCurrent(s)) return false;
                s.put("nextToken", pending);
                db.session(s);
                return true;
              });
      if (!saved) return;
    }
    try {
      JSONObject me = api.request("GET", "/device", candidate, null);
      if (!s.getString("owner").equals(me.optString("accountId"))) throw new IOException("刷新账号不匹配");
    } catch (HttpApi.HttpFailure failure) {
      if (failure.status != 401) throw failure;
      api.request(
          "POST",
          "/device/token/refresh",
          s.getString("token"),
          new JSONObject().put("deviceToken", candidate));
    }
    final String next = candidate;
    db.transaction(
        () -> {
          if (db.isCurrent(s)) {
            s.put("token", next).put("rotatedAt", System.currentTimeMillis());
            s.remove("nextToken");
            db.session(s);
          }
          return null;
        });
  }

  private void revokePending() throws Exception {
    String stored = db.get("revokes", "");
    if (stored.isEmpty()) return;
    com.bazzarhelper.android.data.CredentialCipher c =
        new com.bazzarhelper.android.data.CredentialCipher("com.bazzarhelper.android.revokes");
    JSONArray list = new JSONArray(c.decrypt(stored));
    for (int i = 0; i < list.length(); i++) {
      String token = list.getString(i);
      try {
        api.request("DELETE", "/device/binding", token, null);
      } catch (HttpApi.HttpFailure e) {
        if (e.status != 401) throw e;
      }
      db.transaction(
          () -> {
            JSONArray current = new JSONArray(c.decrypt(db.get("revokes", stored))),
                left = new JSONArray();
            for (int j = 0; j < current.length(); j++)
              if (!token.equals(current.getString(j))) left.put(current.get(j));
            db.put("revokes", c.encrypt(left.toString()));
            return null;
          });
    }
  }

  private JSONObject summarize(String run, List<JSONObject> records) throws Exception {
    JSONObject last = records.get(records.size() - 1),
        b = last.getJSONObject("detail").getJSONObject("battle");
    int wins = 0, losses = 0;
    for (JSONObject e : records)
      if ("win".equals(e.getJSONObject("detail").getJSONObject("battle").getString("result")))
        wins++;
      else losses++;
    JSONObject r =
        new JSONObject()
            .put("runId", run)
            .put("hero", b.optString("playerHero"))
            .put("victories", wins)
            .put("losses", losses)
            .put("finalDay", b.optInt("day"))
            .put("shareEnabled", false)
            .put("sharePolicyVersion", 1);
    try (Cursor c =
        db.getReadableDatabase()
            .rawQuery(
                "SELECT started,mode,ended,facts FROM run_facts WHERE id=?", new String[] {run})) {
      if (c.moveToFirst()) {
        r.put("startedAt", c.getString(0)).put("gameMode", c.getString(1));
        if (!c.isNull(3)) {
          JSONObject facts = new JSONObject(c.getString(3));
          for (String key : new String[] {"victories", "losses", "finalDay"})
            if (facts.has(key)) r.put(key, facts.get(key));
        }
        if (!c.isNull(2))
          r.put("completedAt", c.getString(2))
              .put("result", r.optInt("victories") >= 10 ? "win" : "loss");
      }
    }
    for (String key : new String[] {"dataVersion", "gameDataEtag", "dataVersionSource"})
      if (last.has(key)) r.put(key, last.get(key));
    return r;
  }

  private void upload(JSONObject s, String kind, String id, JSONObject value) throws Exception {
    String owner = s.getString("owner"), fingerprint = digest(value);
    if (fingerprint.equals(db.receipt(owner, kind, id))) return;
    JSONObject body =
        new JSONObject()
            .put("protocolVersion", 3)
            .put("runs", new JSONArray())
            .put("battles", new JSONArray());
    body.getJSONArray(kind.equals("run") ? "runs" : "battles").put(value);
    if (!db.isCurrent(s)) return;
    JSONObject receipt = api.request("POST", "/device/sync", s.getString("token"), body);
    if (receipt.optInt("acceptedRuns", -1) != (kind.equals("run") ? 1 : 0)
        || receipt.optInt("acceptedBattles", -1) != (kind.equals("battle") ? 1 : 0))
      throw new IOException("回执不完整，稍后重试");
    db.transaction(
        () -> {
          if (db.isCurrent(s))
            db.getWritableDatabase()
                .execSQL(
                    "INSERT OR REPLACE INTO receipts(owner,kind,id,fingerprint) VALUES(?,?,?,?)",
                    new Object[] {owner, kind, id, fingerprint});
          return null;
        });
  }

  private static JSONObject select(JSONObject source, String keys) throws JSONException {
    JSONObject v = new JSONObject();
    for (String key : keys.split(" ")) if (source.has(key)) v.put(key, source.get(key));
    return v;
  }

  private static JSONArray cleanArray(JSONArray a, int limit, String keys) throws JSONException {
    JSONArray out = new JSONArray();
    if (a != null)
      for (int i = 0; i < Math.min(a.length(), limit); i++)
        out.put(select(a.getJSONObject(i), keys));
    return out;
  }

  private static void validId(String id) throws IOException {
    if (!id.matches("[A-Za-z0-9_.:-]{1,160}")) throw new IOException("无效记录编号");
  }

  public static String digest(Object value) throws Exception {
    byte[] b =
        MessageDigest.getInstance("SHA-256")
            .digest(canonical(value).getBytes(StandardCharsets.UTF_8));
    StringBuilder out = new StringBuilder();
    for (byte x : b) out.append(String.format(Locale.ROOT, "%02x", x & 255));
    return out.toString();
  }

  private static String canonical(Object v) throws Exception {
    if (v instanceof JSONObject) {
      JSONObject o = (JSONObject) v;
      List<String> keys = new ArrayList<>();
      o.keys().forEachRemaining(keys::add);
      Collections.sort(keys);
      StringJoiner out = new StringJoiner(",", "{", "}");
      for (String k : keys) out.add(JSONObject.quote(k) + ":" + canonical(o.get(k)));
      return out.toString();
    }
    if (v instanceof JSONArray) {
      StringJoiner out = new StringJoiner(",", "[", "]");
      JSONArray a = (JSONArray) v;
      for (int i = 0; i < a.length(); i++) out.add(canonical(a.get(i)));
      return out.toString();
    }
    return v instanceof String ? JSONObject.quote((String) v) : String.valueOf(v);
  }
}
