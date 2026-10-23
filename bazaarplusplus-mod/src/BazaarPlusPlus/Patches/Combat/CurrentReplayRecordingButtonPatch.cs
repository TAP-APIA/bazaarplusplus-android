#nullable enable
using System.Reflection;
using BazaarPlusPlus.Game.CombatReplay;
using BazaarPlusPlus.Game.Settings;
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using HarmonyLib;
using TheBazaar;
using UnityEngine.UI;

namespace BazaarPlusPlus.Patches.Combat;

[HarmonyPatch(
    typeof(BoardRecapReplayButtonsController),
    nameof(BoardRecapReplayButtonsController.Show)
)]
internal static class CurrentReplayRecordingButtonPatch
{
    private static readonly BppFieldAccessor? ReplayButtonField = BppFieldAccessor.Find(
        typeof(BoardRecapReplayButtonsController),
        "ReplayButton"
    );
    private static readonly BppFieldAccessor? RecapButtonField = BppFieldAccessor.Find(
        typeof(BoardRecapReplayButtonsController),
        "RecapButton"
    );
    private static readonly BppFieldAccessor? BackButtonField = BppFieldAccessor.Find(
        typeof(BoardRecapReplayButtonsController),
        "BackButton"
    );

    [HarmonyPostfix]
    private static void Postfix(BoardRecapReplayButtonsController __instance)
    {
        if (__instance == null)
            return;
        var replayButton = ReplayButtonField?.GetValue<Button>(__instance);
        var recapButton = RecapButtonField?.GetValue<Button>(__instance);
        var backButton = BackButtonField?.GetValue<Button>(__instance);
        if (recapButton != null)
            CombatReplayRuntime.Instance?.BindNativeRecapAction(recapButton.onClick.Invoke);
        if (replayButton != null && recapButton != null && backButton != null)
        {
            CurrentReplayRecordingButtonController.BindNativeActions(
                replayButton,
                recapButton,
                backButton
            );
        }
    }
}

[HarmonyPatch(typeof(FightMenuDialog), "Start")]
internal static class CurrentReplayRecordingDockButtonPatch
{
    private static readonly BppFieldAccessor? SettingButtonField = BppFieldAccessor.Find(
        typeof(FightMenuDialog),
        "SettingButton"
    );

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Low)]
    private static void Postfix(FightMenuDialog __instance)
    {
        try
        {
            var settingButtonCustom = SettingButtonField?.GetValue<ButtonCustom>(__instance);
            var settingsButton = settingButtonCustom?.GetButton();
            if (settingsButton != null)
                CurrentReplayRecordingButtonController.Attach(settingsButton);
        }
        catch (Exception ex)
        {
            BppLog.WarnEvent(
                new BppLogEvent(
                    BppLogFeatureScope.Settings,
                    "settings.patch.degraded",
                    storm: ["operation", "reason_code"]
                ),
                ex,
                ("operation", SettingsPatchOperation.DockOpen),
                ("reason_code", SettingsLogReasonCode.PatchException)
            );
        }
    }
}
