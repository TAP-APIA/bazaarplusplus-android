#nullable enable
#pragma warning disable CS0436

using BazaarGameClient.Domain.Models.Cards;
using BazaarPlusPlus.Game.CombatReplay.Bootstrap;
using HarmonyLib;
using TheBazaar;

namespace BazaarPlusPlus.Patches.Combat;

[HarmonyPatch]
internal static class CombatReplayItemPresentationReadinessPatch
{
    [HarmonyPatch(typeof(ItemController), nameof(ItemController.Setup), [typeof(Card)])]
    [HarmonyPostfix]
#if BPP_ANDROID
    private static void TrackItemSetup(Il2CppSystem.Threading.Tasks.Task __result) =>
        ReplayItemPresentationReadiness.Track(__result.AsManagedTask());
#else
    private static void TrackItemSetup(ref Task __result) =>
        __result = ReplayItemPresentationReadiness.Track(__result);
#endif
}

[HarmonyPatch(typeof(ReplayState), "SpawnCombatCards", [])]
internal static class CombatReplayBoardSpawnReadinessPatch
{
    [HarmonyPostfix]
#if BPP_ANDROID
    private static void TrackBoardSpawn(Il2CppSystem.Threading.Tasks.Task __result) =>
        ReplayItemPresentationReadiness.Track(__result.AsManagedTask());
#else
    private static void TrackBoardSpawn(ref Task __result) =>
        __result = ReplayItemPresentationReadiness.Track(__result);
#endif
}
