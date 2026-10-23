#nullable enable
#pragma warning disable CS0436
using BazaarGameShared.Infra.Messages;
using BazaarPlusPlus.Core.Events;
using BazaarPlusPlus.Game.CombatReplay;
using BazaarPlusPlus.GameInterop.Events;
using HarmonyLib;
using TheBazaar;

namespace BazaarPlusPlus.Patches.Combat;

// Combat sim: capture win/loss result
[HarmonyPatch(typeof(CombatSimHandler), "Simulate")]
internal class CombatSimPatch
{
#if BPP_ANDROID
    [HarmonyPrefix]
    private static void Prefix(NetMessageCombatSim message)
    {
        BppPatchHost.Services.EventBus.Publish(new CombatSimObserved { Message = message });
    }
#else
    [HarmonyPrefix]
    private static bool Prefix(
        CombatSimHandler __instance,
        NetMessageCombatSim message,
        CancellationTokenSource cancellationToken,
        ref Task __result
    )
    {
        if (
            CombatReplayRuntime.Instance?.TryDeferCurrentReplaySimulation(
                __instance,
                message,
                cancellationToken,
                out var deferredSimulation
            ) == true
        )
        {
            __result = deferredSimulation;
            return false;
        }

        BppPatchHost.Services.EventBus.Publish(new CombatSimObserved { Message = message });
        return true;
    }
#endif
}

[HarmonyPatch(typeof(FinalBlowSlowDownController), nameof(FinalBlowSlowDownController.Process))]
internal class CombatFrameAdvancePatch
{
#if BPP_ANDROID
    private static IntPtr _observedManager;
    private static bool _observedLateFrames;

    [HarmonyPrefix]
    private static void ObserveManager(int framesLeft)
    {
        // Observation only: retain the native final-hit slowdown and its failure behavior.
        var manager = Singleton<GameServiceManager>.Instance;
        var pointer = manager?.Pointer ?? IntPtr.Zero;
        if (pointer == _observedManager && (framesLeft > 5 || _observedLateFrames))
            return;
        _observedManager = pointer;
        _observedLateFrames = framesLeft <= 5;
        BazaarPlusPlus.Infrastructure.BppStartupTrace.Write(
            "replay:final-blow frames_left=" + framesLeft
            + " frame=" + UnityEngine.Time.frameCount
            + " manager=" + pointer.ToString("x")
            + " initialized=" + (manager != null && manager.IsInitialized)
            + " active_scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
        );
    }
#endif
    [HarmonyPostfix]
    private static void Postfix()
    {
        BppPatchHost.Services.EventBus.Publish(CombatFrameAdvanced.Instance);
    }
}
