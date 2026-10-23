#nullable enable
using BazaarPlusPlus.ModApi;
using BazaarPlusPlus.ModApi.Clients;
using BazaarPlusPlus.ModApi.Http;

namespace BazaarPlusPlus.Infrastructure.Online;

/// <summary>
/// Translates a <see cref="OnlineTransportOutcome{T}"/> that did not succeed into the closed result
/// type each History call site already knows how to render.
///
/// This exists so the transport's failure vocabulary never leaks upward as exceptions or as a new
/// status UI: a timeout, a cooldown rejection and a socket fault all land on the same banner the
/// desktop path shows when the request itself fails.
/// </summary>
internal static class OnlineTransportResults
{
    /// <summary>
    /// Health probe failure. The user code follows the probe's existing vocabulary so
    /// <see cref="HistoryPanelServerHealthReasonClassifier"/> keeps classifying correctly.
    /// </summary>
    internal static ModApiHealthProbeResult ToHealthProbe(
        OnlineTransportOutcome<ModApiHealthProbeResult> outcome
    )
    {
        // Round-trip is unknown for a request that never completed; 0 keeps the formatter's
        // existing "failed after 0 ms" wording rather than inventing a fake latency.
        return ModApiHealthProbeResult.FailureFrom(
            0,
            new ModApiFailure(HealthUserCode(outcome.Failure), outcome.Exception)
        );
    }

    /// <summary>
    /// Account-link redeem failure. Mapped to <see cref="BazaarDbLinkOutcome.Transport"/> so the
    /// banner stays on the existing "offline" copy and nothing persists the linked hint.
    /// </summary>
    internal static BazaarDbLinkResult ToLinkRedeem(
        OnlineTransportOutcome<BazaarDbLinkResult> outcome
    )
    {
        return BazaarDbLinkResult.Failed(
            BazaarDbLinkOutcome.Transport,
            statusCode: null,
            new ModApiFailure(LinkUserCode(outcome.Failure), outcome.Exception)
        );
    }

    private static string HealthUserCode(OnlineTransportFailure? failure) =>
        failure switch
        {
            // The classifier maps anything unrecognized to TransportFailure, which is what a
            // deadline expiry or a socket fault actually is.
            OnlineTransportFailure.Timeout => "transport_timeout",
            OnlineTransportFailure.Throttled => "transport_throttled",
            _ => "transport_error",
        };

    private static string LinkUserCode(OnlineTransportFailure? failure) =>
        failure switch
        {
            // The coordinator's own timeout branch logs RequestTimeout; reuse that vocabulary so
            // both entry points agree on what a deadline expiry means.
            OnlineTransportFailure.Timeout => "request_timeout",
            OnlineTransportFailure.Throttled => "transport_throttled",
            _ => "transport_error",
        };
}
