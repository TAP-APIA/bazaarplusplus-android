#nullable enable
using BazaarPlusPlus.GameInterop;
using BazaarPlusPlus.GameInterop.AssetLoading;
using TheBazaar.AppFramework;
using UnityEngine;
using UnityEngine.UI;

namespace BazaarPlusPlus.Game.CombatStatusBar;

internal sealed class CombatStatusBarNativeSkin
{
    private const string ButtonRoot = "Assets/TheBazaar/Art/UI/Buttons/Btn_Rectangle/";
    private static readonly string[] Addresses =
    [
        ButtonRoot + "Btn_Rct_Frame_S_TUI.png",
        ButtonRoot + "Btn_Rct_Brown_S_Center_Active_TUI.png",
    ];

    private static readonly Color FlatBacking = new(0.23f, 0.15f, 0.09f, 0.94f);
    private static readonly Color FlatFrame = new(0.69f, 0.52f, 0.255f, 0.9f);

    private readonly Sprite[] _sprites;

    private CombatStatusBarNativeSkin(Sprite[] sprites) => _sprites = sprites;

    private Sprite Frame => _sprites[0];
    private Sprite Backing => _sprites[1];
    private bool IsFlat => _sprites.Length == 0;
    internal bool IsValid => _sprites.All(sprite => sprite != null);

    internal static bool TryBeginLoad(out Task<CombatStatusBarNativeSkin?>? pending)
    {
        pending = null;
#if BPP_ANDROID
        // The load awaits Addressable sprite requests, which resolve projected Unity types on a
        // worker thread; that aborts the process on Android. The bar draws a flat plaque instead,
        // since the UI is only built once a skin is ready.
        pending = Task.FromResult<CombatStatusBarNativeSkin?>(new CombatStatusBarNativeSkin([]));
        return true;
#else
        if (!Services.TryGet<AssetLoader>(out var loader) || loader == null)
            return false;

        pending = LoadAsync(loader);
        return true;
#endif
    }

    private static async Task<CombatStatusBarNativeSkin?> LoadAsync(AssetLoader loader)
    {
        // These are the game's Addressable sprites, including their atlas and slice borders.
        // The shared loader pins them in Global scope across lobby/run scene transitions.
        if (Addresses.Any(address => !loader.DoesAddressExist(address)))
            return null;

        var sprites = await Task.WhenAll(
            Addresses.Select(address =>
                NativeGlobalAssetLoader.LoadByAddressAsync<Sprite>(loader, address)
            )
        );
        if (sprites.Any(sprite => sprite == null))
            return null;

        return new CombatStatusBarNativeSkin(sprites.Select(sprite => sprite!).ToArray());
    }

    internal void ApplyPlaque(RectTransform rect)
    {
        if (IsFlat)
        {
            AddFlatLayer("Frame", rect, FlatFrame, Vector2.zero);
            AddFlatLayer("Backing", rect, FlatBacking, new Vector2(2f, 2f));
            return;
        }

        AddLayer("Backing", rect, Backing, new Vector2(2f, 2f));
        AddLayer("Frame", rect, Frame, Vector2.zero);
    }

    private static void AddFlatLayer(string name, RectTransform parent, Color color, Vector2 inset)
    {
        var rect = BppGameObjectFactory.Create(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = inset;
        rect.offsetMax = -inset;
        var image = rect.gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
    }

    private static void AddLayer(string name, RectTransform parent, Sprite sprite, Vector2 inset)
    {
        var rect = BppGameObjectFactory.Create(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = inset;
        rect.offsetMax = -inset;
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.color = Color.white;
        image.type = Image.Type.Sliced;
        // Keep the bevel thin at every dock size; stretch only the nine-slice center.
        image.pixelsPerUnitMultiplier = sprite.rect.height / 38f * 100f / sprite.pixelsPerUnit;
        image.raycastTarget = false;
    }
}
