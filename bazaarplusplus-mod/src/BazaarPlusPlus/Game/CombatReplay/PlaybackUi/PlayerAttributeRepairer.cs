#nullable enable

using System.Globalization;
using System.Reflection;
using BazaarGameClient.Domain.Models;
using BazaarGameShared.Domain.Core.Types;
using BazaarPlusPlus.Game.PvpBattles;
using BazaarPlusPlus.Infrastructure;
using TheBazaar;
using TheBazaar.UI.Components;
#if BPP_ANDROID
using PlayerAttributes = Il2CppSystem.Collections.Generic.Dictionary<
    BazaarGameShared.Domain.Core.Types.EPlayerAttributeType,
    int
>;
#else
using PlayerAttributes = System.Collections.IDictionary;
#endif

namespace BazaarPlusPlus.Game.CombatReplay.PlaybackUi;

internal static class PlayerAttributeRepairer
{
    internal static void EnsureSequencePlayerAttributes(
        CombatSequenceMessages sequence,
        IReplayPlaybackOutcomeSink outcome
    )
    {
        EnsurePlayerAttributes(sequence.SpawnMessage?.Data?.Player, ECombatantId.Player, outcome);
        EnsurePlayerAttributes(
            sequence.SpawnMessage?.Data?.Opponent,
            ECombatantId.Opponent,
            outcome
        );
        EnsurePlayerAttributes(sequence.DespawnMessage?.Data?.Player, ECombatantId.Player, outcome);
        EnsurePlayerAttributes(
            sequence.DespawnMessage?.Data?.Opponent,
            ECombatantId.Opponent,
            outcome
        );
    }

    internal static void EnsureRunPlayerAttributes(IReplayPlaybackOutcomeSink outcome)
    {
        EnsurePlayerAttributes(Data.Run?.Player, ECombatantId.Player, outcome);
        EnsurePlayerAttributes(Data.Run?.Opponent, ECombatantId.Opponent, outcome);
    }

    internal static void RestoreRecordedPlayerAttributes(
        PvpBattleManifest manifest,
        IReplayPlaybackOutcomeSink? outcome = null
    )
    {
        if (manifest == null)
            return;

        var player = Data.Run?.Player;
        if (player == null)
            return;

        try
        {
            var attributes = GetPlayerAttributes(player);
            if (attributes == null)
                return;

            ApplyRecordedAttribute(
                attributes,
                EPlayerAttributeType.Level,
                manifest.Participants.PlayerLevel
            );
            ApplyRecordedAttribute(
                attributes,
                EPlayerAttributeType.Prestige,
                manifest.Participants.PlayerPrestige
            );
            ApplyRecordedAttribute(
                attributes,
                EPlayerAttributeType.Income,
                manifest.Participants.PlayerIncome
            );
            ApplyRecordedAttribute(
                attributes,
                EPlayerAttributeType.Gold,
                manifest.Participants.PlayerGold
            );
            RefreshRecordedAttributePresentation();
        }
        catch (Exception ex)
        {
            outcome?.ReportDegradation(ReplayPlaybackReasonCode.PlayerAttributesUnavailable, ex);
        }
    }

    internal static void RecalculateHealthBarDividers(BoardUIController controller, object? player)
    {
        if (player == null)
            return;

        var healthBar = HealthBarBinder.GetBoardUiHealthBar(controller);
        if (healthBar == null)
            return;

        ApplyHealthBarMaxValue(healthBar, player);
    }

    internal static bool InitializeBoardUiHealthBar(
        BoardUIController controller,
        object? player,
        IReplayPlaybackOutcomeSink? outcome = null
    )
    {
        if (player == null)
            return false;

        var healthBar = HealthBarBinder.GetBoardUiHealthBar(controller);
        if (healthBar == null)
            return false;

        var initMethod = healthBar
            .GetType()
            .GetMethod(
                "Init",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );
        if (initMethod == null)
            return false;

        try
        {
            initMethod.Invoke(healthBar, [player.AsIl2Cpp<Player>() ?? player]);
            ApplyHealthBarMaxValue(healthBar, player);
            return IsHealthBarInitialized(healthBar);
        }
        catch (TargetInvocationException ex)
        {
            outcome?.ReportDegradation(
                ReplayPlaybackReasonCode.PlayerAttributesUnavailable,
                ex.InnerException ?? ex
            );
            return false;
        }
    }

    /// <summary>
    /// Native <c>HealthBarController.Init</c> writes the <c>HealthBarControl</c> field (0x178).
    /// Every later call that walks into the bar — <c>ForceEmptyHealthVisual</c>,
    /// <c>ToggleBarParent</c>, <c>RefillHealthBar</c> — dereferences it, so an <c>Init</c> that
    /// bailed out (or a bar that was registered before <c>Init</c>) throws a native
    /// NullReferenceException from inside <c>ShowEmptyPlayerHealthBar</c>. Treat "not written" as
    /// "not ready" and never hand that bar to the native empty/refill paths.
    /// </summary>
    private static bool IsHealthBarInitialized(object healthBar)
    {
        var control = BppFieldAccessor.Find(healthBar.GetType(), "HealthBarControl")?.GetValue(
            healthBar
        );
        return control != null;
    }

    internal static void UnregisterPlayerPortraitPlacedHandler(
        BoardUIController controller,
        IReplayPlaybackOutcomeSink? outcome = null
    )
    {
        try
        {
#if BPP_ANDROID
            // Remove the original native subscription; converting a fresh managed delegate
            // would create a different native target and could not match that subscription.
            var currentDelegate = BppFieldAccessor.Find(typeof(BoardManager), "_playerPortraitPlaced")
                ?.GetStaticValue<Il2CppSystem.Action>();
            if (currentDelegate == null)
                return;

            foreach (var handler in currentDelegate.GetInvocationList())
            {
                if (
                    handler.Target?.Pointer == controller.Pointer
                    && handler.Method?.Name == "HandleOnPlayerPortraitPlaced"
                )
                    BoardManager.remove_PlayerPortraitPlaced(handler.Cast<Il2CppSystem.Action>());
            }
#else
            var handlerMethod = controller
                .GetType()
                .GetMethod(
                    "HandleOnPlayerPortraitPlaced",
                    BindingFlags.Instance | BindingFlags.NonPublic
                );
            if (handlerMethod == null)
                return;

            var handler = Delegate.CreateDelegate(typeof(Action), controller, handlerMethod);
            var eventField = typeof(BoardManager).GetField(
                "_playerPortraitPlaced",
                BindingFlags.Static | BindingFlags.NonPublic
            );
            if (eventField?.GetValue(null) is Action currentDelegate)
            {
                eventField.SetValue(null, (Action)Delegate.Remove(currentDelegate, handler));
            }
#endif
        }
        catch (Exception ex)
        {
            outcome?.ReportDegradation(ReplayPlaybackReasonCode.PlayerAttributesUnavailable, ex);
        }
    }

    internal static void EnsurePlayerAttributes(
        object? player,
        ECombatantId combatantId,
        IReplayPlaybackOutcomeSink? outcome = null
    )
    {
        if (player == null)
            return;

        try
        {
            var attributes = GetPlayerAttributes(player);
            if (attributes == null)
                return;

            EnsurePlayerAttributeDefaults(attributes);
            EnsureHealthMax(attributes);
        }
        catch (Exception ex)
        {
            outcome?.ReportDegradation(ReplayPlaybackReasonCode.PlayerAttributesUnavailable, ex);
        }
    }

    private static void EnsureHealthMax(PlayerAttributes attributes)
    {
        if (
            ContainsAttribute(attributes, EPlayerAttributeType.HealthMax)
            && Convert.ToInt32(
                attributes[EPlayerAttributeType.HealthMax],
                CultureInfo.InvariantCulture
            ) > 0
        )
            return;

        if (!ContainsAttribute(attributes, EPlayerAttributeType.Health))
            return;

        var healthValue = Convert.ToInt32(
            attributes[EPlayerAttributeType.Health],
            CultureInfo.InvariantCulture
        );
        if (healthValue <= 0)
            return;

        attributes[EPlayerAttributeType.HealthMax] = healthValue;
    }

    private static void EnsurePlayerAttributeDefaults(PlayerAttributes attributes)
    {
        foreach (EPlayerAttributeType attributeType in Enum.GetValues(typeof(EPlayerAttributeType)))
        {
            EnsurePlayerAttribute(
                attributes,
                attributeType,
                attributeType == EPlayerAttributeType.Level ? 1 : 0
            );
        }
    }

    private static void EnsurePlayerAttribute(
        PlayerAttributes attributes,
        EPlayerAttributeType attributeType,
        int defaultValue
    )
    {
        if (!ContainsAttribute(attributes, attributeType))
            attributes[attributeType] = defaultValue;
    }

    private static void ApplyRecordedAttribute(
        PlayerAttributes attributes,
        EPlayerAttributeType attributeType,
        int? value
    )
    {
        if (!value.HasValue)
            return;

        attributes[attributeType] = value.Value;
    }

    private static void RefreshRecordedAttributePresentation()
    {
        foreach (var prestigeBar in UnityEngine.Object.FindObjectsOfType<PrestigeBarController>())
        {
            prestigeBar?.ImmediateUpdate();
        }

        var updateUiFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var bank in UnityEngine.Object.FindObjectsOfType<BankToyController>())
        {
            bank?.GetType().GetMethod("UpdateUI", updateUiFlags)?.Invoke(bank, null);
        }
    }

    private static void ApplyHealthBarMaxValue(object healthBar, object player)
    {
        var healthMax = TryGetPlayerAttribute(player, EPlayerAttributeType.HealthMax);
        if (!healthMax.HasValue)
            return;

        var updateMaxHealth = healthBar
            .GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, "UpdateMaxHealth", StringComparison.Ordinal))
                    return false;

                var parameters = method.GetParameters();
                return parameters.Length == 3
                    && parameters[0].ParameterType == typeof(uint)
                    && parameters[1].ParameterType == typeof(uint)
                    && parameters[2].ParameterType == typeof(bool);
            });
        if (updateMaxHealth == null)
            return;

        updateMaxHealth.Invoke(healthBar, [healthMax.Value, healthMax.Value, false]);
    }

    private static uint? TryGetPlayerAttribute(object player, EPlayerAttributeType attributeType)
    {
        var attributes = GetPlayerAttributes(player);
        if (attributes == null)
            return null;
        if (!ContainsAttribute(attributes, attributeType))
            return null;

        return Convert.ToUInt32(attributes[attributeType], CultureInfo.InvariantCulture);
    }

    private static PlayerAttributes? GetPlayerAttributes(object player)
    {
        return BppFieldAccessor.Find(player.GetType(), "Attributes")
            ?.GetValue<PlayerAttributes>(player);
    }

    private static bool ContainsAttribute(
        PlayerAttributes attributes,
        EPlayerAttributeType attributeType
    )
    {
#if BPP_ANDROID
        return attributes.ContainsKey(attributeType);
#else
        return attributes.Contains(attributeType);
#endif
    }
}
