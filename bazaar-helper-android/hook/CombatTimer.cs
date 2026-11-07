using BazaarGameShared.Infra.Messages;
using HarmonyLib;
using TheBazaar;
namespace BazaarHelper;

// Read-only playback clock: exactly the same 50ms logical-frame convention as the original timer.
internal static class CombatTimer
{
    private static readonly CombatFrameClock Clock = new();
    private static int displayed = -1;
    internal static void Install(Harmony harmony)
    {
        Patch(harmony, typeof(FinalBlowSlowDownController), "Process", nameof(Frame), typeof(int));
        Patch(harmony, typeof(CombatSimHandler), "CancelCombat", nameof(Hide));
        Patch(harmony, typeof(GameServiceManager), "FinishCombat", nameof(End), typeof(BazaarBattleService.Models.BazaarMatchHistory.EVictoryCondition));
        Patch(harmony, typeof(GameServiceManager), "OnCloseRun", nameof(Hide));
    }
    private static void Patch(Harmony harmony, Type type, string name, string callback, params Type[] args)
    {
        var target = AccessTools.Method(type, name, args) ?? throw new MissingMethodException(type.FullName, name);
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(CombatTimer), callback));
    }
    internal static void Begin(NetMessageCombatSim message)
    {
        try
        {
            Clock.Begin(message.Data?.Frames?.Count ?? 0);
            displayed = -1;
            Publish();
        }
        catch (Exception e) { Report(e); }
    }
    private static void Frame()
    {
        try
        {
            Clock.Advance();
            Publish();
        }
        catch (Exception e) { Report(e); }
    }
    private static void End()
    {
        try
        {
            Clock.End();
            Publish();
        }
        catch (Exception e) { Report(e); }
    }
    internal static void Hide()
    {
        try
        {
            Clock.Reset();
            displayed = -1;
            NativeBridge.Timer(-1);
        }
        catch (Exception e) { Report(e); }
    }
    private static void Publish()
    {
        if (!Clock.HasCombat || displayed == Clock.ElapsedMilliseconds)
            return;
        displayed = Clock.ElapsedMilliseconds;
        NativeBridge.Timer(displayed);
    }
    private static void Report(Exception e) => Recorder.Log.LogWarning("Battle timer unavailable: " + e.GetType().Name);
}
