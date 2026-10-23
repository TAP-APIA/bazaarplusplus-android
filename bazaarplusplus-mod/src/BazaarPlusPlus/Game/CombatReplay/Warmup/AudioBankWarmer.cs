#nullable enable
using System.Diagnostics;
using BazaarPlusPlus.Infrastructure;
using FMOD.Studio;
using TheBazaar;
using TheBazaar.AppFramework;

namespace BazaarPlusPlus.Game.CombatReplay.Warmup;

internal static class AudioBankWarmer
{
    /// <summary>
    /// Warms the replay's board and soundtrack banks before playback starts.
    ///
    /// A warmup that cannot be completed is a quality degradation, never a silent success: every
    /// step that gives up reports a reason code and, when it has one, the exception behind it.
    /// </summary>
    internal static async Task WarmAudioBanksAsync(IReplayPlaybackOutcomeSink outcome)
    {
        var stopwatch = Stopwatch.StartNew();
        var stats = new ReplayAudioWarmupStats();
        var failures = new AudioWarmupFailures();
        try
        {
            var soundManager = Services.Get<SoundManager>();
            if (soundManager == null)
            {
                ReportAudioFailure(outcome, failures, "sound-manager-missing");
                return;
            }

            var collectionManager = Services.Get<CollectionManager>();

            var boardAssets = UnityEngine
                .Object.FindObjectsOfType<HeroBoardController>(true)
                .Where(controller =>
                    controller != null && controller.gameObject.scene.rootCount > 0
                )
                .Select(controller => controller.AssociatedDataSO)
                .Where(asset => asset != null)
                .Distinct()
                .ToList();

            SoundtrackWarmer.AddBoardAsset(
                boardAssets,
                await SoundtrackWarmer.TryGetPlayerBoardAsync(collectionManager, outcome, failures)
            );
            SoundtrackWarmer.AddBoardAsset(
                boardAssets,
                await SoundtrackWarmer.TryGetOpponentBoardAsync(
                    collectionManager,
                    outcome,
                    failures
                )
            );

            if (boardAssets.Count == 0)
            {
                ReportAudioFailure(outcome, failures, "no-board-assets");
                return;
            }

            foreach (var boardAsset in boardAssets)
            {
                await SoundtrackWarmer.WarmBoardAudioAsync(
                    soundManager,
                    boardAsset!,
                    stats,
                    outcome,
                    failures
                );
            }

            await SoundtrackWarmer.WarmSoundtracksAsync(
                soundManager,
                collectionManager,
                boardAssets,
                stats,
                outcome,
                failures
            );
        }
        catch (Exception ex)
        {
            ReportAudioFailure(outcome, failures, "warmup-exception", ex);
        }
        finally
        {
            stopwatch.Stop();
            ReplayWarmupLogging.AudioCompleted(
                outcome.BattleId,
                stopwatch.ElapsedMilliseconds,
                stats,
                failures
            );
            if (failures.Count == 0)
            {
                // The acceptance signal: every bank this replay needed is resident, so the
                // recording can be judged on its own merits rather than by absence of failure.
                ReplayWarmupLogging.AudioSucceeded(
                    outcome.BattleId,
                    stopwatch.ElapsedMilliseconds,
                    stats
                );
            }
        }
    }

    /// <summary>
    /// Hands the warmed playback back to the game's own audio path: unpause the paused game, resume
    /// the buses, drop SFX events still tracked from the live fight, and reapply the saved SFX volume.
    /// </summary>
    internal static void EnsureAudioReadyForPlayback(IReplayPlaybackOutcomeSink outcome)
    {
        var failures = new AudioWarmupFailures();
        try
        {
            var gameServiceManager = Singleton<GameServiceManager>.Instance;
            if (gameServiceManager?.GamePaused == true)
            {
                gameServiceManager.PauseOrUnpauseGame(toPauseOrUnpause: false);
            }

            var soundManager = Services.Get<SoundManager>();
            if (soundManager == null)
            {
                ReportAudioFailure(outcome, failures, "sound-manager-missing-at-playback");
                return;
            }

            soundManager.PauseBusses(isPausing: false);

            StopAllTrackedSfxEventInstances(soundManager, outcome, failures);
            ReassertSfxVolumeFromPreferences(outcome, failures);
        }
        catch (Exception ex)
        {
            ReportAudioFailure(outcome, failures, "playback-readiness-exception", ex);
        }
    }

    /// <summary>
    /// Reports one audio warmup failure once, naming the step that gave up.
    ///
    /// The first failure decides the playback's primary reason code, so the stage has to identify
    /// the real cause rather than the last step that noticed it.
    /// </summary>
    private static void ReportAudioFailure(
        IReplayPlaybackOutcomeSink outcome,
        AudioWarmupFailures failures,
        string stage,
        Exception? exception = null
    )
    {
        failures.Add(stage, exception);
#if BPP_ANDROID
        BppStartupTrace.Write(
            "replay:audio-failure stage=" + stage
            + (exception == null ? " no_exception=true" : " exception=" + DescribeException(exception))
        );
#endif
        ReplayWarmupLogging.AudioStageFailed(stage, exception);
        outcome.ReportDegradation(ReplayPlaybackReasonCode.AudioWarmupFailed, exception);
    }

    /// <summary>
    /// Stops and releases the SFX event instances the live fight left in
    /// <c>SFXPlayer.sfxEventInstances</c>, so the replay starts from a clean SFX state.
    ///
    /// On IL2CPP that member is a generated property holding an
    /// <c>Il2CppSystem.Collections.Generic.Dictionary</c>; the old <c>GetField</c> lookup found no
    /// field at all, so this step reported a warmup failure on every Android replay. Reading the
    /// projected property restores the desktop behaviour instead of skipping the step.
    /// </summary>
    private static void StopAllTrackedSfxEventInstances(
        SoundManager soundManager,
        IReplayPlaybackOutcomeSink outcome,
        AudioWarmupFailures failures
    )
    {
        var sfxPlayer = soundManager.SFXPlayer;
        if (sfxPlayer == null)
        {
            ReportAudioFailure(outcome, failures, "sfx-player-missing");
            return;
        }

#if BPP_ANDROID
        Il2CppSystem.Collections.Generic.Dictionary<string, EventInstance>? tracked;
        try
        {
            tracked = sfxPlayer.sfxEventInstances;
        }
        catch (Exception ex)
        {
            ReportAudioFailure(outcome, failures, "sfx-event-instances-read", ex);
            return;
        }

        if (tracked == null)
        {
            ReportAudioFailure(outcome, failures, "sfx-event-instances-null");
            return;
        }

        try
        {
            if (tracked.Count == 0)
                return;

            // The projected key collection exposes CopyTo only; enumerating it directly is not
            // supported, and mutating the dictionary while holding a snapshot is what we want.
            var keys = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(
                tracked.Count
            );
            tracked.Keys.CopyTo(keys, 0);
            for (var index = 0; index < keys.Length; index++)
            {
                try
                {
                    var instance = tracked[keys[index]];
                    if (!instance.isValid())
                        continue;

                    instance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
                    instance.release();
                }
                catch (Exception ex)
                {
                    ReportAudioFailure(outcome, failures, "sfx-instance-release", ex);
                }
            }

            tracked.Clear();
        }
        catch (Exception ex)
        {
            ReportAudioFailure(outcome, failures, "sfx-release-enumeration", ex);
        }
#else
        try
        {
            var accessor = BppFieldAccessor.Find(sfxPlayer.GetType(), "sfxEventInstances");
            if (accessor?.GetValue(sfxPlayer) is not Dictionary<string, EventInstance> tracked)
            {
                ReportAudioFailure(outcome, failures, "sfx-event-instances-absent");
                return;
            }

            if (tracked.Count == 0)
                return;

            foreach (var key in tracked.Keys.ToList())
            {
                try
                {
                    var instance = tracked[key];
                    if (!instance.isValid())
                        continue;

                    instance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
                    instance.release();
                }
                catch (Exception ex)
                {
                    ReportAudioFailure(outcome, failures, "sfx-instance-release", ex);
                }
            }

            tracked.Clear();
        }
        catch (Exception ex)
        {
            ReportAudioFailure(outcome, failures, "sfx-release-enumeration", ex);
        }
#endif
    }

    private static void ReassertSfxVolumeFromPreferences(
        IReplayPlaybackOutcomeSink outcome,
        AudioWarmupFailures failures
    )
    {
        try
        {
            var prefs = PlayerPreferences.Data;
            if (prefs == null)
                return;

#if BPP_ANDROID
            // SetVolume(VolumeType, float) and the nested VolumeType enum are public on the
            // IL2CPP proxy, so both are called directly. Reflecting here boxed a proxy enum and
            // handed it to the generated wrapper, which is exactly the binding that cannot be
            // relied on across the managed/native boundary.
            SoundManager.SetVolume(SoundManager.VolumeType.SFX, prefs.VolumeSfx);
#else
            var setVolume = typeof(SoundManager).GetMethod(
                "SetVolume",
                System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public
            );
            var volumeTypeEnum = typeof(SoundManager).GetNestedType(
                "VolumeType",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
            );
            if (setVolume == null || volumeTypeEnum == null)
            {
                ReportAudioFailure(outcome, failures, "sfx-volume-api-absent");
                return;
            }

            var sfxValue = Enum.Parse(volumeTypeEnum, "SFX");
            setVolume.Invoke(null, new[] { sfxValue, (object)prefs.VolumeSfx });
#endif
        }
        catch (Exception ex)
        {
            ReportAudioFailure(outcome, failures, "sfx-volume-reassert", ex);
        }
    }

    /// <summary>Renders an exception type and every inner exception for the device trace.</summary>
    private static string DescribeException(Exception exception)
    {
        var description = new System.Text.StringBuilder();
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (description.Length != 0)
                description.Append(" <- ");

            description
                .Append(current.GetType().FullName)
                .Append(": ")
                .Append(current.Message);
        }
        return description.ToString();
    }
}

/// <summary>
/// Records which audio warmup steps fell short and why, so the single terminal playback event can
/// name the real cause instead of only the last step that noticed the shortfall.
/// </summary>
internal sealed class AudioWarmupFailures
{
    private readonly List<string> _stages = new();

    internal int Count => _stages.Count;

    internal string? PrimaryStage => _stages.Count == 0 ? null : _stages[0];

    internal void Add(string stage, Exception? exception)
    {
        _stages.Add(
            exception == null
                ? stage
                : stage
                    + ":"
                    + exception.GetType().Name
                    + (
                        exception.InnerException == null
                            ? string.Empty
                            : "/inner=" + exception.InnerException.GetType().Name
                    )
        );
    }

    internal string JoinedStages() => string.Join(",", _stages);
}
