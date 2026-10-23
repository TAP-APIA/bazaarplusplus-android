#nullable enable
#if !BPP_ANDROID
using System.Reflection;
#endif
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarPlusPlus.GameInterop.AssetLoading;
using TheBazaar;
using TheBazaar.AppFramework;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace BazaarPlusPlus.Game.CombatReplay.Warmup;

internal static class CombatVfxWarmer
{
    internal static async Task WarmCombatVfxAsync(
        CombatSequenceMessages sequence,
        ReplayWarmupStats stats,
        IReplayPlaybackOutcomeSink outcome
    )
    {
        Services.TryGet<AssetLoader>(out var assetLoader);
        Services.TryGet<VFXManager>(out var vfxManager);
        if (assetLoader == null || vfxManager == null)
        {
            outcome.ReportDegradation(ReplayPlaybackReasonCode.CombatVfxWarmupFailed);
            return;
        }

        var actionTypes = sequence
            .CombatMessage.Data.Frames.ToManaged().SelectMany(frame =>
                frame?.Events?.ToManaged() ?? Enumerable.Empty<ICombatSimEvent>()
            )
            .OfType<CombatSimEventEffectExecuted>()
            .Select(evt => DTOUtils.GetActionType(evt.ActionType))
            .Where(action => action != ActionType.Unknown)
            .Distinct()
            .ToList();
        var vfxSemaphore = new SemaphoreSlim(WarmupConstants.ReplayWarmupConcurrency);
        var vfxTasks = new List<Task>();

        foreach (var action in actionTypes)
        {
            vfxTasks.Add(
                WarmActionVfxAsync(assetLoader, vfxManager, action, vfxSemaphore, stats, outcome)
            );
        }

        foreach (
            var overrideKey in sequence
                .CombatMessage.Data.VfxKeys.ToManaged().Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal)
        )
        {
            vfxTasks.Add(
                WarmOverrideVfxAsync(
                    assetLoader,
                    vfxManager,
                    actionTypes,
                    overrideKey,
                    vfxSemaphore,
                    stats,
                    outcome
                )
            );
        }
        await Task.WhenAll(vfxTasks);
    }

    private static async Task WarmActionVfxAsync(
        AssetLoader assetLoader,
        VFXManager vfxManager,
        ActionType action,
        SemaphoreSlim semaphore,
        ReplayWarmupStats stats,
        IReplayPlaybackOutcomeSink outcome
    )
    {
        var vfxConfig = GetVfxConfig(vfxManager);
        if (vfxConfig == null)
        {
            await WarmVfxReferenceAsync(
                assetLoader,
                vfxManager.GetVFX(action),
                semaphore,
                stats,
                outcome
            );
            return;
        }

        if (TryIsActionAttributeMapped(vfxConfig, action))
        {
            foreach (var size in PresentationWarmer.WarmupCardSizes)
            {
                await WarmVfxReferenceAsync(
                    assetLoader,
                    TryGetMappedActionVfx(vfxConfig, size, action),
                    semaphore,
                    stats,
                    outcome
                );
            }
        }

        await WarmVfxReferenceAsync(
            assetLoader,
            vfxManager.GetVFX(action),
            semaphore,
            stats,
            outcome
        );
    }

    private static async Task WarmOverrideVfxAsync(
        AssetLoader assetLoader,
        VFXManager vfxManager,
        IReadOnlyCollection<ActionType> actionTypes,
        string overrideKey,
        SemaphoreSlim semaphore,
        ReplayWarmupStats stats,
        IReplayPlaybackOutcomeSink outcome
    )
    {
        var vfxConfig = GetVfxConfig(vfxManager);
        if (vfxConfig == null)
            return;

        foreach (var action in actionTypes)
        {
            foreach (var size in PresentationWarmer.WarmupCardSizes)
            {
                await WarmVfxReferenceAsync(
                    assetLoader,
                    await TryGetOverrideActionVfxAsync(vfxConfig, action, size, overrideKey),
                    semaphore,
                    stats,
                    outcome
                );
            }
        }
    }

    /// <summary>
    /// Reads <c>VFXManager.vfxManagerSO</c>.
    ///
    /// On IL2CPP the game's <c>[SerializeField]</c> is projected as a property, not a
    /// <c>FieldInfo</c>, so a bare <c>GetField</c> silently returned <c>null</c> and disabled the
    /// mapped-action VFX warmup on device. <see cref="BppFieldAccessor"/> resolves either shape.
    /// </summary>
    private static object? GetVfxConfig(VFXManager vfxManager)
    {
        return BppFieldAccessor.Find(vfxManager.GetType(), "vfxManagerSO")?.GetValue(vfxManager);
    }

    private static bool TryIsActionAttributeMapped(object vfxConfig, ActionType action)
    {
#if BPP_ANDROID
        return vfxConfig is BazaarVFXManagerSO config && config.IsActionAttributeMapped(action);
#else
        var method = vfxConfig
            .GetType()
            .GetMethod(
                "IsActionAttributeMapped",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );
        return method?.Invoke(vfxConfig, new object[] { action }) as bool? == true;
#endif
    }

    private static AssetReference? TryGetMappedActionVfx(
        object vfxConfig,
        ECardSize size,
        ActionType action
    )
    {
#if BPP_ANDROID
        return vfxConfig is BazaarVFXManagerSO config ? config.GetVFX(size, action) : null;
#else
        var method = vfxConfig
            .GetType()
            .GetMethod(
                "GetVFX",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(ECardSize), typeof(ActionType) },
                null
            );
        return method?.Invoke(vfxConfig, new object[] { size, action }) as AssetReference;
#endif
    }

    private static async Task<AssetReference?> TryGetOverrideActionVfxAsync(
        object vfxConfig,
        ActionType action,
        ECardSize size,
        string overrideKey
    )
    {
#if BPP_ANDROID
        // The config is a projected BazaarVFXManagerSO, so the override lookup is called directly.
        // Reflecting over the proxy binds the generated wrapper instead of the game method.
        if (vfxConfig is not BazaarVFXManagerSO config)
            return null;

        return await config
            .GetActionOverrideVFX(action, size, overrideKey)
            .AsManagedTask<AssetReference>();
#else
        var method = vfxConfig
            .GetType()
            .GetMethod(
                "GetActionOverrideVFX",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(ActionType), typeof(ECardSize), typeof(string) },
                null
            );
        if (method == null)
            return null;

        var taskObject = method.Invoke(vfxConfig, new object[] { action, size, overrideKey });
        if (taskObject is Task<AssetReference> typedTask)
            return await typedTask;

        if (taskObject is not Task task)
            return taskObject as AssetReference;

        await task;
        return task.GetType()
                .GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(task) as AssetReference;
#endif
    }

    private static async Task WarmVfxReferenceAsync(
        AssetLoader assetLoader,
        AssetReference? assetReference,
        SemaphoreSlim semaphore,
        ReplayWarmupStats stats,
        IReplayPlaybackOutcomeSink outcome
    )
    {
        if (assetReference == null || !assetReference.RuntimeKeyIsValid())
            return;

        var key = !string.IsNullOrWhiteSpace(assetReference.AssetGUID)
            ? assetReference.AssetGUID
            : assetReference.ToString();
        if (!WarmupCache.TryReserveCacheKey(WarmupCache.PrewarmedVfxKeys, key))
        {
            stats.VfxSkipped++;
            return;
        }

        await semaphore.WaitAsync();
        try
        {
            _ = await NativeGlobalAssetLoader.LoadByReferenceAsync<GameObject>(
                assetLoader,
                assetReference
            );
            stats.VfxPrewarmed++;
        }
        catch (Exception ex)
        {
            WarmupCache.ReleaseCacheKey(WarmupCache.PrewarmedVfxKeys, key);
            stats.VfxFailed++;
            outcome.ReportDegradation(ReplayPlaybackReasonCode.CombatVfxWarmupFailed, ex);
            ReplayWarmupLogging.AssetSkipped(
                ReplayWarmupStage.CombatVfx,
                key,
                ReplayWarmupAssetReasonCode.AssetLoadFailed,
                ex
            );
        }
        finally
        {
            semaphore.Release();
        }
    }
}
