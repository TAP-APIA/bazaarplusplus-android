#nullable enable
using BazaarPlusPlus.Infrastructure;

using UnityEngine;

namespace BazaarPlusPlus.Game.VoiceSubtitles;

internal sealed class VoiceLineDisplayDispatcher : MonoBehaviour
{
    private void Update()
    {
        // A managed exception escaping a per-frame callback becomes std::terminate across the
        // IL2CPP boundary and kills the process with no managed stack.
        try
        {
            TickGuarded();
        }
        catch (Exception ex)
        {
            BppStartupTrace.Write("VoiceLineDisplayDispatcher-update-exception:" + ex.GetType().FullName + " | " + ex.Message);
        }
    }

    private void TickGuarded()
    {
        VoiceLineDisplay.ProcessQueuedShows();
    }
}
