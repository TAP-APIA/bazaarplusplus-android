#nullable enable
namespace BazaarPlusPlus.Localization;

// Language for BazaarPlusPlus' own UI. Auto keeps the historical behaviour of following the
// game's language preference and only falls back to the OS language when the game stored none,
// which is what Android builds that never persist PreferencesData do.
internal enum BppUiLanguage
{
    Auto,
    English,
    Chinese,
    German,
    Portuguese,
    Korean,
    Italian,
}
