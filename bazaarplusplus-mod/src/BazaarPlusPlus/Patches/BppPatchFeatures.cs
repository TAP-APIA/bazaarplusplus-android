#nullable enable
using BazaarPlusPlus.Game.CollectionPanel;
using BazaarPlusPlus.Game.EventPreview;
using BazaarPlusPlus.Game.HistoryPanel;
#if !BPP_ANDROID
using BazaarPlusPlus.Game.PostCombatImpact;
#endif
using BazaarPlusPlus.Game.Screenshots;

namespace BazaarPlusPlus.Patches;

internal sealed class BppPatchFeatures
{
    internal BppPatchFeatures(
        EncounterPreviewModule encounterPreview,
        IEndOfRunCaptureWorkflow endOfRunCaptureWorkflow
#if !BPP_ANDROID
        , PostCombatImpactModule postCombatImpact
#endif
    )
    {
        EncounterPreview =
            encounterPreview ?? throw new ArgumentNullException(nameof(encounterPreview));
        EndOfRunCaptureWorkflow =
            endOfRunCaptureWorkflow
            ?? throw new ArgumentNullException(nameof(endOfRunCaptureWorkflow));
#if !BPP_ANDROID
        PostCombatImpact =
            postCombatImpact ?? throw new ArgumentNullException(nameof(postCombatImpact));
#endif
    }

    internal HistoryPanelMenuEntry HistoryMenu { get; } = new();
    internal CollectionInspectionModule CollectionInspection { get; } = new();

    internal EncounterPreviewModule EncounterPreview { get; }
    internal IEndOfRunCaptureWorkflow EndOfRunCaptureWorkflow { get; }
#if !BPP_ANDROID
    internal PostCombatImpactModule PostCombatImpact { get; }
#endif
}
