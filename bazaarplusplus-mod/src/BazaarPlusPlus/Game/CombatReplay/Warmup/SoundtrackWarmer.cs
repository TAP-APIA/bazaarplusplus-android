#nullable enable
using Assets.Scripts.Audio;
using TheBazaar;
using TheBazaar.Assets.Scripts.ScriptableObjectsScripts;

namespace BazaarPlusPlus.Game.CombatReplay.Warmup;

internal static class SoundtrackWarmer
{
    internal static async Task WarmSoundtracksAsync(
        SoundManager soundManager,
        CollectionManager? collectionManager,
        IReadOnlyCollection<BoardAssetDataSO> boardAssets,
        ReplayAudioWarmupStats stats,
        IReplayPlaybackOutcomeSink outcome,
        AudioWarmupFailures failures
    )
    {
        var warmedAny = false;
        var warmedSoundtracks = new HashSet<string>(StringComparer.Ordinal);
        warmedAny |= await WarmSoundtrackAsync(
            soundManager,
            await TryGetSoundtrackAsync(collectionManager, outcome, failures),
            stats,
            warmedSoundtracks,
            setPlayingSoundtrack: true,
            outcome: outcome,
            failures: failures
        );

        foreach (var boardAsset in boardAssets)
        {
            warmedAny |= await WarmSoundtrackAsync(
                soundManager,
                boardAsset.soundtrack,
                stats,
                warmedSoundtracks,
                setPlayingSoundtrack: soundManager.PlayingSoundTrackSO == null,
                outcome: outcome,
                failures: failures
            );
        }

        if (!warmedAny)
        {
            stats.SoundtrackBanksSkipped++;
            ReportFailure(
                outcome,
                failures,
                "soundtrack-not-warmed",
                ReplayPlaybackReasonCode.SoundtrackWarmupFailed
            );
        }
    }

    internal static async Task WarmBoardAudioAsync(
        SoundManager soundManager,
        BoardAssetDataSO boardAsset,
        ReplayAudioWarmupStats stats,
        IReplayPlaybackOutcomeSink outcome,
        AudioWarmupFailures failures
    )
    {
        if (string.IsNullOrWhiteSpace(boardAsset.boardBank))
        {
            stats.BoardBanksSkipped++;
            ReportFailure(
                outcome,
                failures,
                "board-metadata-bank-unset",
                ReplayPlaybackReasonCode.SoundtrackWarmupFailed
            );
            return;
        }

        if (string.IsNullOrWhiteSpace(boardAsset.boardAssetBank))
        {
            stats.BoardBanksSkipped++;
            ReportFailure(
                outcome,
                failures,
                "board-asset-bank-unset",
                ReplayPlaybackReasonCode.SoundtrackWarmupFailed
            );
            return;
        }

        var wasMetadataLoaded = soundManager.IsBankLoaded(boardAsset.boardBank, isMetadata: false);
        var wasAssetLoaded = soundManager.IsBankLoaded(
            boardAsset.boardAssetBank,
            isMetadata: false
        );

        bool loaded;
        try
        {
            loaded = await soundManager.LoadBankAsync(
                FModBank.EBankType.SFX,
                boardAsset.boardBank,
                boardAsset.boardAssetBank
            );
        }
        catch (Exception ex)
        {
            stats.BoardBanksFailed++;
            ReportFailure(
                outcome,
                failures,
                "board-bank-load",
                ReplayPlaybackReasonCode.SoundtrackWarmupFailed,
                ex
            );
            return;
        }

        if (!loaded)
        {
            stats.BoardBanksFailed++;
            ReportFailure(
                outcome,
                failures,
                "board-bank-load-returned-false",
                ReplayPlaybackReasonCode.SoundtrackWarmupFailed
            );
            return;
        }

        if (wasMetadataLoaded && wasAssetLoaded)
            stats.BoardBanksAlreadyLoaded++;
        else
            stats.BoardBanksLoaded++;
    }

    internal static void AddBoardAsset(
        ICollection<BoardAssetDataSO> boardAssets,
        BoardAssetDataSO? boardAsset
    )
    {
        if (
            boardAsset == null
            || boardAssets.Any(existing => ReferenceEquals(existing, boardAsset))
        )
            return;

        boardAssets.Add(boardAsset);
    }

    internal static async Task<BoardAssetDataSO?> TryGetPlayerBoardAsync(
        CollectionManager? collectionManager,
        IReplayPlaybackOutcomeSink outcome,
        AudioWarmupFailures failures
    )
    {
        if (collectionManager == null)
            return null;

        try
        {
#if BPP_ANDROID
            // IL2CPP Nullable<T> is a proxy object; the optional default is null,
            // not an empty native nullable, and fails before entering the game method.
            return await collectionManager.GetEquippedBoard(
                new Il2CppSystem.Nullable<Il2CppSystem.Threading.CancellationToken>()
            );
#else
            return await collectionManager.GetEquippedBoard();
#endif
        }
        catch (Exception ex)
        {
            ReportFailure(
                outcome,
                failures,
                "player-board-lookup",
                ReplayPlaybackReasonCode.SoundtrackWarmupFailed,
                ex
            );
            return null;
        }
    }

    internal static async Task<BoardAssetDataSO?> TryGetOpponentBoardAsync(
        CollectionManager? collectionManager,
        IReplayPlaybackOutcomeSink outcome,
        AudioWarmupFailures failures
    )
    {
        var loadout = Data.SimPvpOpponent?.PlayerLoadout;
        if (collectionManager == null || loadout == null)
            return null;

        try
        {
#pragma warning disable CS0618
#if BPP_ANDROID
            return await collectionManager.GetEquippedBoard(
                loadout,
                new Il2CppSystem.Nullable<Il2CppSystem.Threading.CancellationToken>()
            );
#else
            return await collectionManager.GetEquippedBoard(loadout);
#endif
#pragma warning restore CS0618
        }
        catch (Exception ex)
        {
            ReportFailure(
                outcome,
                failures,
                "opponent-board-lookup",
                ReplayPlaybackReasonCode.SoundtrackWarmupFailed,
                ex
            );
            return null;
        }
    }

    private static async Task<bool> WarmSoundtrackAsync(
        SoundManager soundManager,
        SoundtrackSO? soundtrack,
        ReplayAudioWarmupStats stats,
        ISet<string> warmedSoundtracks,
        bool setPlayingSoundtrack,
        IReplayPlaybackOutcomeSink outcome,
        AudioWarmupFailures failures
    )
    {
        if (soundtrack == null)
            return false;

        var key = !string.IsNullOrWhiteSpace(soundtrack.SoundtrackPath)
            ? soundtrack.SoundtrackPath
            : soundtrack.name;
        if (!string.IsNullOrWhiteSpace(key) && warmedSoundtracks.Contains(key))
            return true;

        var loadedSoundtrack = await TryLoadSoundtrackAssetAsync(soundtrack, outcome, failures);
        if (loadedSoundtrack == null)
        {
            stats.SoundtrackBanksFailed++;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(key))
            warmedSoundtracks.Add(key);

        // MusicTracks is an Il2CppReferenceArray / Track[]; Length is its length on both runtimes.
        var musicTracks = loadedSoundtrack.MusicTracks;
        if (musicTracks == null || musicTracks.Length == 0)
        {
            stats.SoundtrackBanksSkipped++;
            ReportFailure(
                outcome,
                failures,
                "soundtrack-has-no-tracks",
                ReplayPlaybackReasonCode.SoundtrackWarmupFailed
            );
            return false;
        }

        if (setPlayingSoundtrack)
            soundManager.PlayingSoundTrackSO = loadedSoundtrack;

        for (var trackIndex = 0u; trackIndex < musicTracks.Length; trackIndex++)
        {
            await WarmSoundtrackTrackAsync(
                soundManager,
                loadedSoundtrack,
                trackIndex,
                stats,
                outcome,
                failures
            );
        }

        return true;
    }

    private static async Task<SoundtrackSO?> TryGetSoundtrackAsync(
        CollectionManager? collectionManager,
        IReplayPlaybackOutcomeSink outcome,
        AudioWarmupFailures failures
    )
    {
        if (collectionManager == null)
            return null;

        try
        {
#if BPP_ANDROID
            var soundtrackAsset = await collectionManager.GetEquippedSoundtrack(
                new Il2CppSystem.Nullable<Il2CppSystem.Threading.CancellationToken>()
            );
#else
            var soundtrackAsset = await collectionManager.GetEquippedSoundtrack();
#endif
            return soundtrackAsset != null ? soundtrackAsset.SoundtrackObject : null;
        }
        catch (Exception ex)
        {
            ReportFailure(
                outcome,
                failures,
                "equipped-soundtrack-lookup",
                ReplayPlaybackReasonCode.SoundtrackWarmupFailed,
                ex
            );
            return null;
        }
    }

    /// <summary>
    /// Loads the soundtrack prefab and, on success, its authoritative asset.
    ///
    /// A failed addressable load is not fatal by itself: the already-loaded <c>SoundtrackSO</c>
    /// still names real banks, so playback continues with it and the shortfall is reported.
    /// </summary>
    private static async Task<SoundtrackSO?> TryLoadSoundtrackAssetAsync(
        SoundtrackSO soundtrack,
        IReplayPlaybackOutcomeSink outcome,
        AudioWarmupFailures failures
    )
    {
        if (string.IsNullOrWhiteSpace(soundtrack.SoundtrackPath))
            return soundtrack;

        try
        {
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<SoundtrackSO>(
                soundtrack.SoundtrackPath
            );
            await handle.Task;
            if (
                handle.Status
                == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded
            )
                return handle.Result;

            ReportFailure(
                outcome,
                failures,
                "soundtrack-asset-load-status",
                ReplayPlaybackReasonCode.SoundtrackWarmupFailed
            );
            return soundtrack;
        }
        catch (Exception ex)
        {
            ReportFailure(
                outcome,
                failures,
                "soundtrack-asset-load",
                ReplayPlaybackReasonCode.SoundtrackWarmupFailed,
                ex
            );
            return soundtrack;
        }
    }

    private static async Task WarmSoundtrackTrackAsync(
        SoundManager soundManager,
        SoundtrackSO soundtrack,
        uint trackIndex,
        ReplayAudioWarmupStats stats,
        IReplayPlaybackOutcomeSink outcome,
        AudioWarmupFailures failures
    )
    {
        // TrackBankName(uint, bool) is a public game method on both runtimes, so it is called
        // directly; reflecting over the IL2CPP proxy would bind the generated wrapper instead.
        var metadataBank = soundtrack.TrackBankName(trackIndex, isAssetBank: false);
        var assetBank = soundtrack.TrackBankName(trackIndex, isAssetBank: true);
        if (string.IsNullOrWhiteSpace(metadataBank) || string.IsNullOrWhiteSpace(assetBank))
        {
            stats.SoundtrackBanksSkipped++;
            ReportFailure(
                outcome,
                failures,
                "soundtrack-track-bank-unresolved",
                ReplayPlaybackReasonCode.SoundtrackWarmupFailed
            );
            return;
        }

        var wasMetadataLoaded = soundManager.IsBankLoaded(metadataBank, isMetadata: false);
        var wasAssetLoaded = soundManager.IsBankLoaded(assetBank, isMetadata: false);

        bool loaded;
        try
        {
            loaded = await soundManager.LoadBankAsync(
                FModBank.EBankType.Music,
                metadataBank,
                assetBank
            );
        }
        catch (Exception ex)
        {
            stats.SoundtrackBanksFailed++;
            ReportFailure(
                outcome,
                failures,
                "soundtrack-bank-load",
                ReplayPlaybackReasonCode.SoundtrackWarmupFailed,
                ex
            );
            return;
        }

        if (!loaded)
        {
            stats.SoundtrackBanksFailed++;
            ReportFailure(
                outcome,
                failures,
                "soundtrack-bank-load-returned-false",
                ReplayPlaybackReasonCode.SoundtrackWarmupFailed
            );
            return;
        }

        if (wasMetadataLoaded && wasAssetLoaded)
            stats.SoundtrackBanksAlreadyLoaded++;
        else
            stats.SoundtrackBanksLoaded++;
    }

    private static void ReportFailure(
        IReplayPlaybackOutcomeSink outcome,
        AudioWarmupFailures failures,
        string stage,
        ReplayPlaybackReasonCode reasonCode,
        Exception? exception = null
    )
    {
        failures.Add(stage, exception);
        ReplayWarmupLogging.AudioStageFailed(stage, exception);
        outcome.ReportDegradation(reasonCode, exception);
    }
}
