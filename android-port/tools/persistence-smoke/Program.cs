using BazaarPlusPlus.Game.CombatReplay;
using BazaarPlusPlus.Game.PvpBattles;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

foreach (var failManifest in new[] { false, true })
{
    using var gate = new ReplayPayloadOperationGate();
    var writes = new List<string>();
    var producer = Environment.CurrentManagedThreadId;
    var worker = producer;
    using var queue = new CombatReplayPersistenceQueue(
        _ => { worker = Environment.CurrentManagedThreadId; writes.Add("payload"); },
        _ => { if (failManifest) throw new IOException("injected manifest failure"); writes.Add("manifest"); },
        _ => writes.Add("rollback"), gate);
    queue.Enqueue(new PvpReplayPayload { BattleId = "test" }, new PvpBattleManifest { BattleId = "test" });
    CombatReplayPersistenceResult result = default;
    Require(SpinWait.SpinUntil(() => queue.TryDequeueResult(out result), TimeSpan.FromSeconds(5)), "Android queue did not complete");
    Require(worker != producer, "Save ran on producer thread");
    Require(result.Succeeded != failManifest, "Wrong completion status");
    Require(writes.SequenceEqual(failManifest ? new[] { "payload", "rollback" } : new[] { "payload", "manifest" }), "Wrong persistence/rollback order");
    Require(!queue.HasPendingPersistence, "Pending count did not drain");
}
using (var gate = new ReplayPayloadOperationGate())
{
    var count = 0;
    var queue = new CombatReplayPersistenceQueue(_ => { }, _ => Interlocked.Increment(ref count), _ => { }, gate);
    for (var i = 0; i < 20; i++)
        queue.Enqueue(new PvpReplayPayload { BattleId = i.ToString() }, new PvpBattleManifest { BattleId = i.ToString() });
    queue.Dispose();
    Require(count == 20, "Shutdown lost queued writes");
}
Console.WriteLine("PASS: Android background persistence, rollback, result counts, and shutdown drain.");
