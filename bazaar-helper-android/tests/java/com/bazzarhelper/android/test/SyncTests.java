package com.bazzarhelper.android.test;

import android.app.Instrumentation;
import android.content.Context;
import android.os.Bundle;
import com.bazzarhelper.android.data.*;
import com.bazzarhelper.android.sync.*;
import java.util.*;
import org.json.*;

public final class SyncTests extends Instrumentation {
  private int checks;
  private RecordDatabase db;
  private SyncEngine engine;
  private Fake api;

  @Override
  public void onCreate(Bundle b) {
    super.onCreate(b);
    start();
  }

  @Override
  public void onStart() {
    Bundle result = new Bundle();
    try {
      run();
      result.putString(
          "stream",
          "PASS "
              + checks
              + " checks: durable ownership, idempotency, receipts, logout race, encryption,"
              + " canonical fingerprints\n");
      finish(-1, result);
    } catch (Throwable e) {
      result.putString(
          "stream",
          "FAIL "
              + e.getClass().getSimpleName()
              + ": "
              + e.getMessage()
              + "\n"
              + android.util.Log.getStackTraceString(e));
      finish(0, result);
    } finally {
      if (db != null) db.close();
    }
  }

  private void yes(boolean condition, String message) {
    checks++;
    if (!condition) throw new AssertionError(message);
  }

  private JSONObject binding(String owner, String stamp) throws Exception {
    JSONObject s =
        new JSONObject()
            .put("owner", owner)
            .put("generation", stamp)
            .put("token", "test-token-" + stamp)
            .put("checkedAt", System.currentTimeMillis());
    db.session(s);
    db.getWritableDatabase()
        .execSQL(
            "INSERT OR REPLACE INTO sessions(stamp,owner) VALUES(?,?)",
            new Object[] {stamp, owner});
    return s;
  }

  private JSONObject battle(String id) throws Exception {
    return new JSONObject(
        "{\"runId\":\"run-test\",\"detail\":{\"battle\":{\"battleId\":\""
            + id
            + "\",\"recordedAt\":\"2026-11-07T00:00:00Z\",\"result\":\"win\",\"day\":1,\"playerHero\":\"Vanessa\"},\"cards\":[{\"side\":\"opponent\",\"templateId\":\"fish\",\"name\":\"Fish\",\"privateAccount\":\"must-strip\"}],\"metrics\":[]},\"rawPayload\":\"must-strip\"}");
  }

  private void run() throws Exception {
    Context c = getTargetContext();
    String name = "sync-tests-" + UUID.randomUUID();
    db = new RecordDatabase(c, name, "com.bazzarhelper.android.tests");
    api = new Fake();
    engine = new SyncEngine(db, api);
    JSONObject a = binding("account-a", "a");
    boolean refused = false;
    try {
      engine.abandonLogin();
    } catch (IllegalStateException expected) {
      refused = true;
    }
    yes(refused, "bound session cannot be abandoned by the login screen");
    engine.beginRun(new JSONObject().put("runId", "run-test").put("gameMode", "Ranked"));
    engine.capture("a", battle("battle-1"));
    yes(db.records("account-a", null).size() == 1, "capture saved");
    yes(
        !db.records("account-a", null).get(0).toString().contains("must-strip"),
        "whitelist strips raw identity");
    yes(!db.get("credentials", "").contains("test-token"), "token encrypted");
    engine.capture("a", battle("battle-1"));
    yes(db.records("account-a", null).size() == 1, "duplicate capture is idempotent");
    binding("account-b", "b");
    engine.capture("a", battle("battle-late"));
    engine.capture("", battle("battle-anonymous"));
    yes(db.records("account-b", null).isEmpty(), "old and anonymous records not claimed");
    yes(db.records("account-a", null).size() == 2, "settlement stamp owns late save");
    binding("account-a", "a2");
    api.incomplete = true;
    try {
      engine.sync();
      throw new AssertionError("bad receipt accepted");
    } catch (java.io.IOException expected) {
    }
    yes(db.receipt("account-a", "run", "run-test").isEmpty(), "bad receipt retains queue");
    api.incomplete = false;
    engine.sync();
    yes(api.battles == 2, "both battle envelopes uploaded");
    yes(!api.lastRun.optBoolean("shareEnabled", true), "private by default");
    int requests = api.calls;
    engine.sync();
    yes(api.calls == requests, "acknowledged payloads not resent");
    JSONObject enriched = battle("battle-1");
    enriched.getJSONObject("detail").getJSONArray("cards").getJSONObject(0).put("tier", "Gold");
    engine.capture("a2", enriched);
    engine.sync();
    yes(api.battles == 3, "changed payload retries with new fingerprint");
    engine.capture("a2", battle("battle-race"));
    api.during = () -> binding("account-b", "b2");
    engine.sync();
    yes(
        db.receipt("account-a", "battle", "battle-race").isEmpty(),
        "stale response cannot acknowledge after switch");
    binding("account-a", "a3");
    api.during = () -> binding("account-b", "b3");
    api.unauthorized = true;
    try {
      engine.sync();
    } catch (HttpApi.HttpFailure expected) {
    }
    yes(
        "account-b".equals(db.session().optString("owner")),
        "stale 401 cannot log out new account");
    api.unauthorized = false;
    engine.logout();
    yes(db.session().optString("token").isEmpty(), "logout clears locally immediately");
    binding("account-b", "b4");
    engine.logout();
    yes(!db.get("revokes", "").contains("test-token"), "revocation retry tokens encrypted");
    engine.sync();
    yes(api.revokes == 2, "multiple offline logout tokens revoked");
    yes(
        SyncEngine.digest(new JSONObject("{\"a\":1,\"b\":[2,3]}"))
            .equals(SyncEngine.digest(new JSONObject("{\"b\":[2,3],\"a\":1}"))),
        "fingerprint independent of key order");
    JSONObject rotated = binding("account-b", "rotation");
    rotated.put("rotatedAt", System.currentTimeMillis() - 8 * 86400000L);
    db.session(rotated);
    api.serverToken = rotated.getString("token");
    api.fakeOwner = "account-b";
    api.loseRotationResponse = true;
    try {
      engine.sync();
      throw new AssertionError("lost response not surfaced");
    } catch (java.io.IOException expected) {
    }
    yes(
        !db.session().optString("nextToken").isEmpty(),
        "rotation candidate durable before request");
    yes(
        db.session().optString("token").equals(rotated.getString("token")),
        "lost response preserves previous local token");
    api.loseRotationResponse = false;
    engine.sync();
    yes(
        db.session().optString("token").equals(api.serverToken),
        "lost refresh response recovered via new token");
    yes(db.session().optString("nextToken").isEmpty(), "recovered rotation committed atomically");
    engine.logout();
    engine.sync();
    JSONObject qr = engine.login();
    yes(qr.has("qrCode"), "binding protocol returns QR");
    api.fakeOwner = "account-c";
    api.serverToken = db.session().getString("token");
    yes(engine.poll(), "binding poll validates bearer identity");
    yes("account-c".equals(db.session().optString("owner")), "binding stores account scope");
    int count = db.records("account-a", null).size();
    db.close();
    db = new RecordDatabase(c, name, "com.bazzarhelper.android.tests");
    yes(db.records("account-a", null).size() == count, "record survives database reopen");
    db.close();
    db = null;
    new java.io.File(c.getNoBackupFilesDir(), name + ".sqlite").delete();
    new java.io.File(c.getNoBackupFilesDir(), name + ".sqlite.upload.lock").delete();
  }

  private interface Action {
    void run() throws Exception;
  }

  private final class Fake implements SyncEngine.Transport {
    int calls, battles, revokes;
    boolean incomplete, unauthorized, loseRotationResponse;
    String serverToken = "", fakeOwner = "account-b";
    JSONObject lastRun;
    Action during;

    public JSONObject request(String method, String path, String token, JSONObject body)
        throws Exception {
      calls++;
      if (during != null) {
        Action action = during;
        during = null;
        action.run();
      }
      if (unauthorized) throw new HttpApi.HttpFailure(401);
      if (path.equals("/binding-sessions")) {
        yes(
            body.optInt("protocolVersion") == 3 && body.optString("deviceToken").length() >= 24,
            "binding v3 identity");
        return new JSONObject()
            .put("nonce", "test-nonce")
            .put("expiresAt", "2026-11-07T23:59:00Z")
            .put("qrCode", "data:image/png;base64,test");
      }
      if (path.startsWith("/binding-sessions/")) return new JSONObject().put("status", "bound");
      if (path.equals("/device")) {
        if (!token.equals(serverToken)) throw new HttpApi.HttpFailure(401);
        return new JSONObject().put("accountId", fakeOwner).put("accountAlias", "Test account");
      }
      if (path.equals("/device/token/refresh")) {
        serverToken = body.getString("deviceToken");
        if (loseRotationResponse) throw new java.io.IOException("simulated dropped response");
        return new JSONObject().put("expiresAt", "2027-01-01T00:00:00Z");
      }
      if (method.equals("DELETE")) {
        revokes++;
        return new JSONObject();
      }
      if (path.equals("/device/sync")) {
        JSONArray r = body.getJSONArray("runs"), b = body.getJSONArray("battles");
        if (r.length() > 0) lastRun = r.getJSONObject(0);
        battles += b.length();
        return new JSONObject()
            .put("acceptedRuns", incomplete ? 0 : r.length())
            .put("acceptedBattles", b.length());
      }
      throw new AssertionError("Unexpected HTTP route");
    }
  }
}
