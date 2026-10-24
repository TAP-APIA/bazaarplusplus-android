#nullable enable

namespace BazaarPlusPlus.Game.LiveBuildPanel.Data;

// Selects what the Matches card body shows: a single guidance line (no run / no candidates / no
// corpus data for the hero / no matching build) or the detailed per-recommendation stat rows (has
// recommendation).
internal enum LiveBuildMatchesState
{
    NoRun,
    NoCandidates,

    // The corpus carries no ten-win sample for this hero at all, so no selection could ever match.
    // Reported apart from NoRecommendation, which means the corpus covers the hero but the current
    // candidates are not in any published build.
    NoHeroData,
    NoRecommendation,
    HasRecommendation,
}
