using System.Reflection;
using System.Runtime.Loader;
using BazaarPlusPlus.Game.PvpBattles;

var packaged = Path.GetFullPath(args[0]);
var build = Path.GetFullPath(args[1]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (var directory in new[] { packaged, build })
    {
        var path = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
// Keep the game's same-name proxy loaded while the portable library resolves its aliases.
AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[2]));
var library = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(packaged, "BazaarPlusPlus.ModApi.dll"));
var types = library.GetTypes();
var codec = types.Single(type => type.FullName == "BazaarPlusPlus.ModApi.MessagePackGzipCodec");
var sample = new PvpReplayPayload
{
    BattleId = "android-codec-smoke", Version = 1,
    SpawnMessageBytes = new byte[] { 0, 1, 2, 255 },
    CombatMessageBytes = new byte[] { 8, 13, 21 },
    DespawnMessageBytes = new byte[] { 34, 55 }
};
var bytes = (byte[])codec.GetMethod("Serialize")!.MakeGenericMethod(typeof(PvpReplayPayload)).Invoke(null, new object[] { sample })!;
var parameters = new object?[] { bytes, null, null, null, null };
var success = (bool)codec.GetMethod("TryDeserialize")!.MakeGenericMethod(typeof(PvpReplayPayload)).Invoke(null, parameters)!;
if (!success || parameters[1] is not PvpReplayPayload actual
    || actual.BattleId != sample.BattleId || actual.Version != sample.Version
    || !actual.SpawnMessageBytes.SequenceEqual(sample.SpawnMessageBytes)
    || !actual.CombatMessageBytes.SequenceEqual(sample.CombatMessageBytes)
    || !actual.DespawnMessageBytes.SequenceEqual(sample.DespawnMessageBytes))
    throw new Exception($"Round trip failed: {parameters[2]} {parameters[3]}");
Console.WriteLine($"PASS: {types.Length} portable types loaded; replay gzip round trip ({bytes.Length} bytes), alongside native MessagePack proxy.");
