using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using BazaarGameShared.Infra.Messages;
using TheBazaar;
namespace BazaarHelper;

[BepInPlugin("com.bazzarhelper.android.recorder", "BazaarHelper Recorder", "0.1.2")]
public sealed class Plugin : BasePlugin
{
    public override void Load()
    {
        Recorder.Log = Log;
        AbiProbe.Verify();
        Log.LogInfo("Native optional field ABI probe passed (empty + enum zero)");
        var dispatch = AccessTools.Method(typeof(NetMessageProcessor), "Receive", new[] { typeof(INetMessage), typeof(bool) }) ?? AccessTools.Method(typeof(NetMessageProcessor), "ReceiveOrQueue", new[] { typeof(INetMessage) }) ?? throw new MissingMethodException("NetMessageProcessor dispatch");
        var simulate = AccessTools.Method(typeof(CombatSimHandler), "Simulate", new[] { typeof(NetMessageCombatSim), typeof(Il2CppSystem.Threading.CancellationTokenSource) }) ?? throw new MissingMethodException("CombatSimHandler.Simulate");
        var harmony = new Harmony("com.bazzarhelper.android.recorder");
        harmony.Patch(dispatch, prefix: new HarmonyMethod(typeof(Plugin), nameof(Observe)));
        harmony.Patch(simulate, prefix: new HarmonyMethod(typeof(Plugin), nameof(Combat)));
        try
        {
            CombatTimer.Install(harmony);
            Log.LogInfo("Battle timer ready: 50ms logical frames, display only");
        }
        catch (Exception error)
        {
            Log.LogWarning("Battle timer hooks unavailable: " + error.GetType().Name);
        }
        _ = NativeBridge.Session();
        NativeBridge.Status("战绩采集已就绪");
        Log.LogInfo("BazaarHelper recorder ready: dispatch + combat; no gameplay modifications");
    }
    private static void Observe(object[] __args)
    {
        try
        {
            if (__args.Length > 1 && __args[1] is true)
            {
                Recorder.BlockPlayback();
                return;
            }
            if (__args[0] is INetMessage message)
                Recorder.Observe(message);
        }
        catch (Exception e) { Recorder.Failure(e); }
    }
    private static void Combat(NetMessageCombatSim message)
    {
        try
        {
            CombatTimer.Begin(message);
            Recorder.Settle(message);
        }
        catch (Exception e) { Recorder.Failure(e); }
    }
}
