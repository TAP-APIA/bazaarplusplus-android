#nullable enable
using System.Reflection;
using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Core.Types;
using HarmonyLib;
using TheBazaar.AppFramework;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace BazaarPlusPlus.GameInterop.AssetLoading;

// The shared native prefab/reference seam. Callers own every successfully returned root.
internal static class NativeCardPrefabLoader
{
    private static readonly MethodInfo? InstantiateAssetMethod = AccessTools.Method(
        typeof(AssetLoader),
        "InstantiateAssetAsyncByReference"
    );
    private static readonly BppFieldAccessor? SmallItemAsset = BppFieldAccessor.Find(
        typeof(AssetLoader),
        "SmallCardUIAssetRef"
    );
    private static readonly BppFieldAccessor? MediumItemAsset = BppFieldAccessor.Find(
        typeof(AssetLoader),
        "MediumCardUIAssetRef"
    );
    private static readonly BppFieldAccessor? LargeItemAsset = BppFieldAccessor.Find(
        typeof(AssetLoader),
        "LargeCardUIAssetRef"
    );
    private static readonly BppFieldAccessor? SkillAsset = BppFieldAccessor.Find(
        typeof(AssetLoader),
        "SkillUIAssetRef"
    );

    internal static async Task<GameObject?> LoadPrefabAsync(TCardBase template)
    {
        if (
            !Services.TryGet<AssetLoader>(out var loader)
            || loader == null
            || ResolveAssetField(template)?.GetValue<AssetReference>(loader) is not { } reference
        )
            return null;
        return await NativeGlobalAssetLoader.LoadByReferenceAsync<GameObject>(loader, reference);
    }

    internal static async Task<GameObject?> RentInactiveAsync(
        TCardBase template,
        Transform parent,
        CancellationToken token = default
    )
    {
        if (!Services.TryGet<AssetLoader>(out var loader) || loader == null)
            return null;
#if BPP_ANDROID
        if (ResolveAssetField(template)?.GetValue<AssetReference>(loader) is not { } reference)
            return null;
        token.ThrowIfCancellationRequested();
        GameObject? root = null;
        try
        {
            root = await loader
                .InstantiateAssetAsyncByReference(
                    reference,
                    new Il2CppSystem.Nullable<AssetScope>()
                )
                .AsManagedTask();
            token.ThrowIfCancellationRequested();
            if (root == null)
                return null;
            root.SetActive(false);
            root.transform.SetParent(parent, false);
            return root;
        }
        catch
        {
            if (root != null)
                UnityEngine.Object.Destroy(root);
            throw;
        }
#else
        var reference = ResolveAssetField(template)?.GetValue(loader);
        if (
            reference == null
            || !NativeAssetLoaderInvocation.TryBuildArguments(
                InstantiateAssetMethod,
                reference,
                NativeAssetScopeIntent.Current,
                out var arguments
            )
        )
            return null;
        token.ThrowIfCancellationRequested();
        GameObject? root = null;
        try
        {
            if (InstantiateAssetMethod!.Invoke(loader, arguments) is not Task<GameObject> task)
                return null;
            root = await task;
            token.ThrowIfCancellationRequested();
            if (root == null)
                return null;
            root.SetActive(false);
            root.transform.SetParent(parent, false);
            return root;
        }
        catch
        {
            if (root != null)
                UnityEngine.Object.Destroy(root);
            throw;
        }
#endif
    }

    private static BppFieldAccessor? ResolveAssetField(TCardBase template) =>
        template.Type switch
        {
            ECardType.Skill => SkillAsset,
            ECardType.Item when template.Size == ECardSize.Small => SmallItemAsset,
            ECardType.Item when template.Size == ECardSize.Medium => MediumItemAsset,
            ECardType.Item when template.Size == ECardSize.Large => LargeItemAsset,
            _ => null,
        };
}
