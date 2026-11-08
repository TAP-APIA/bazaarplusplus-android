package com.bazzarhelper.android.data;

import android.content.Context;
import android.database.Cursor;
import android.database.sqlite.SQLiteDatabase;
import android.database.sqlite.SQLiteOpenHelper;
import java.io.File;
import java.util.ArrayList;
import java.util.List;
import org.json.JSONObject;

/** One WAL database shared by the UI and game processes; ownership is immutable at settlement. */
public final class RecordDatabase extends SQLiteOpenHelper {
  private final CredentialCipher cipher;
  public final File directory;

  public RecordDatabase(Context context) {
    this(context, "records", "com.bazzarhelper.android.credentials");
  }

  public RecordDatabase(Context context, String name, String alias) {
    super(
        context,
        new File(context.getNoBackupFilesDir(), name + ".sqlite").getAbsolutePath(),
        null,
        4);
    directory = context.getNoBackupFilesDir();
    cipher = new CredentialCipher(alias);
    setWriteAheadLoggingEnabled(true);
  }

  @Override
  public void onCreate(SQLiteDatabase db) {
    db.execSQL("CREATE TABLE kv(k TEXT PRIMARY KEY,v TEXT NOT NULL)");
    db.execSQL("CREATE TABLE sessions(stamp TEXT PRIMARY KEY,owner TEXT NOT NULL)");
    db.execSQL(
        "CREATE TABLE run_facts(id TEXT PRIMARY KEY,started TEXT NOT NULL,mode TEXT,ended"
            + " TEXT,facts TEXT,hero TEXT,owner TEXT)");
    db.execSQL(
        "CREATE TABLE battles(owner TEXT NOT NULL,id TEXT NOT NULL,run_id TEXT NOT NULL,recorded"
            + " TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(owner,id))");
    db.execSQL("CREATE INDEX battle_owner_run ON battles(owner,run_id,recorded)");
    db.execSQL(
        "CREATE TABLE receipts(owner TEXT NOT NULL,kind TEXT NOT NULL,id TEXT NOT NULL,fingerprint"
            + " TEXT NOT NULL,PRIMARY KEY(owner,kind,id))");
  }

  @Override
  public void onUpgrade(SQLiteDatabase db, int oldVersion, int newVersion) {
    if (oldVersion == 1 && newVersion >= 2) {
      db.execSQL("ALTER TABLE run_facts ADD COLUMN facts TEXT");
      oldVersion = 2;
    }
    if (oldVersion == 2 && newVersion >= 3) {
      // Battles recorded before the hook learned to tell monster/PvE fights apart carry the placeholder
      // hero EHero.Common; they are not player-vs-player and must never reach the uploader.
      // Only drop rows the server already acknowledged: anything still waiting to upload keeps its
      // place in the outbox, so this cleanup can never lose a battle that was never sent.
      int removed =
          db.delete(
              "battles",
              "payload LIKE ? AND EXISTS(SELECT 1 FROM receipts r WHERE r.kind='battle' AND r.id=battles.id)",
              new String[] {"%\"opponentHero\":\"Common\"%"});
      com.bazzarhelper.android.Diag.i("db", "dropped " + removed + " uploaded non-player battles");
      oldVersion = 3;
    }
    if (oldVersion == 3 && newVersion == 4) {
      // A run is uploaded from its own row now, so it needs the hero shown before the first battle and
      // the owner it belongs to (run_facts previously had neither).
      db.execSQL("ALTER TABLE run_facts ADD COLUMN hero TEXT");
      db.execSQL("ALTER TABLE run_facts ADD COLUMN owner TEXT");
      return;
    }
    throw new IllegalStateException("Unsupported database migration");
  }

  public interface Transaction<T> {
    T run() throws Exception;
  }

  public synchronized <T> T transaction(Transaction<T> action) throws Exception {
    SQLiteDatabase db = getWritableDatabase();
    db.beginTransaction();
    try {
      T result = action.run();
      db.setTransactionSuccessful();
      return result;
    } finally {
      db.endTransaction();
    }
  }

  public String get(String key, String fallback) {
    try (Cursor c =
        getReadableDatabase().rawQuery("SELECT v FROM kv WHERE k=?", new String[] {key})) {
      return c.moveToFirst() ? c.getString(0) : fallback;
    }
  }

  public void put(String key, String value) {
    getWritableDatabase()
        .execSQL("INSERT OR REPLACE INTO kv(k,v) VALUES(?,?)", new Object[] {key, value});
  }

  /** Outbox lock scoped to this database file; the helper and game processes still share it. */
  public File uploadLock() {
    return new File(getDatabaseName() + ".upload.lock");
  }

  public JSONObject session() throws Exception {
    String v = get("credentials", "");
    return v.isEmpty() ? new JSONObject() : new JSONObject(cipher.decrypt(v));
  }

  public void session(JSONObject value) throws Exception {
    put("credentials", cipher.encrypt(value.toString()));
  }

  public boolean isCurrent(JSONObject expected) throws Exception {
    JSONObject now = session();
    return !expected.optString("generation").isEmpty()
        && expected.optString("generation").equals(now.optString("generation"))
        && expected.optString("token").equals(now.optString("token"));
  }

  public List<JSONObject> records(String owner, String run) throws Exception {
    List<JSONObject> result = new ArrayList<>();
    String sql =
        "SELECT payload FROM battles WHERE owner=?"
            + (run == null
                ? " ORDER BY recorded DESC LIMIT 200"
                : " AND run_id=? ORDER BY recorded");
    try (Cursor c =
        getReadableDatabase()
            .rawQuery(sql, run == null ? new String[] {owner} : new String[] {owner, run})) {
      while (c.moveToNext()) result.add(new JSONObject(c.getString(0)));
    }
    return result;
  }

  /** Runs to sync: those with battles for this owner plus runs stamped with it before their first fight. */
  public List<String> runsForUpload(String owner) {
    List<String> result = new ArrayList<>();
    try (Cursor c =
        getReadableDatabase()
            .rawQuery(
                "SELECT id FROM (SELECT run_id AS id FROM battles WHERE owner=? UNION"
                    + " SELECT id FROM run_facts WHERE owner=?) GROUP BY id ORDER BY id",
                new String[] {owner, owner})) {
      while (c.moveToNext()) result.add(c.getString(0));
    }
    return result;
  }

  public List<String> runIds(String owner) {
    List<String> result = new ArrayList<>();
    try (Cursor c =
        getReadableDatabase()
            .rawQuery(
                "SELECT run_id FROM battles WHERE owner=? GROUP BY run_id ORDER BY max(recorded)"
                    + " DESC",
                new String[] {owner})) {
      while (c.moveToNext()) result.add(c.getString(0));
    }
    return result;
  }

  public String owner(String stamp) {
    try (Cursor c =
        getReadableDatabase()
            .rawQuery("SELECT owner FROM sessions WHERE stamp=?", new String[] {stamp})) {
      return c.moveToFirst() ? c.getString(0) : null;
    }
  }

  public String receipt(String owner, String kind, String id) {
    try (Cursor c =
        getReadableDatabase()
            .rawQuery(
                "SELECT fingerprint FROM receipts WHERE owner=? AND kind=? AND id=?",
                new String[] {owner, kind, id})) {
      return c.moveToFirst() ? c.getString(0) : "";
    }
  }
}
