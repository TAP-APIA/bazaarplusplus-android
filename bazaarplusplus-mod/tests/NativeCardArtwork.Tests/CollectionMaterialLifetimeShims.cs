namespace UnityEngine
{
    internal class MonoBehaviour;

    internal sealed class Material
    {
        public bool Destroyed { get; set; }
    }
}

namespace TheBazaar.UI
{
    internal sealed class CardPreviewBase
    {
        public UnityEngine.GameObject gameObject { get; } = new();
        public BazaarPlusPlus.Game.CollectionPanel.Grid.CollectionPanelOwnedMarker? Marker;
        public UnityEngine.Material? _cardMaterial;
        public CardImage _cardImage = new();

        public T? GetComponent<T>()
            where T : class => Marker as T;
    }

    internal sealed class CardImage
    {
        public object? texture;
        public UnityEngine.Material? material;
    }
}

namespace BazaarPlusPlus.Game.CollectionPanel.Grid
{
    internal sealed class CollectionNativeCardPreviewOwner
    {
        public void OnNativeDestroyed(TheBazaar.UI.CardPreviewBase card) { }
    }

    internal sealed class CollectionCardCacheSession
    {
        public LeaseCounter ArtCache { get; } = new();
        public LeaseCounter MaterialCache { get; } = new();
    }

    internal sealed class LeaseCounter
    {
        public int Releases { get; private set; }

        public void Release(string artKey) => Releases++;
    }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class HarmonyPatch : Attribute
    {
        public Type Type { get; }
        public string Method { get; }
        public HarmonyPatch(Type type, string method) { Type = type; Method = method; }
    }

    internal sealed class HarmonyPrefix : Attribute;

    internal sealed class HarmonyPriority : Attribute
    {
        public HarmonyPriority(int priority) { }
    }

    internal static class Priority
    {
        public const int First = 800;
        public const int Normal = 400;
    }
}
