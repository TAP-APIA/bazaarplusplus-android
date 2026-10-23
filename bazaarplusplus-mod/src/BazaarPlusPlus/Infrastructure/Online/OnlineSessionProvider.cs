#nullable enable
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using BazaarPlusPlus.ModApi;
using BazaarPlusPlus.ModApi.Clients;
using BazaarPlusPlus.ModApi.Http;

namespace BazaarPlusPlus.Infrastructure.Online;

/// <summary>
/// Owns the History module's mod-API session and BazaarDB link client.
///
/// The desktop path builds these in the plugin entry point. On the Android port that phase is
/// still commented out, so the History panel would otherwise mount local-only and every online
/// action would report "unavailable". This provider lets the panel acquire the same clients itself,
/// which keeps the entry-point change optional instead of a prerequisite.
///
/// Construction is lazy and happens on the first online action rather than during startup: a
/// failure here then degrades one feature instead of the whole plugin, and a user who never opens
/// an online action never pays for a socket pool or a TLS backend probe.
/// </summary>
internal sealed class OnlineSessionProvider : IDisposable
{
    private readonly object _sync = new();
    private readonly AndroidOnlineTransport _transport;
    private readonly string _productVersion;
    private readonly string _apiBaseUrl;
    private ModApiSession? _session;
    private BazaarDbLinkClient? _linkClient;
    private OnlineSessionFailure? _failure;
    private bool _disposed;

    internal OnlineSessionProvider(
        AndroidOnlineTransport transport,
        string apiBaseUrl,
        string productVersion
    )
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _apiBaseUrl = apiBaseUrl ?? throw new ArgumentNullException(nameof(apiBaseUrl));
        _productVersion = productVersion ?? throw new ArgumentNullException(nameof(productVersion));
    }

    /// <summary>
    /// The transport the lazily-built session uses. Exposed so call sites can put their own
    /// requests under the same deadline and cooldown policy instead of duplicating the limits.
    /// </summary>
    internal AndroidOnlineTransport Transport => _transport;

    /// <summary>
    /// Injected session (desktop entry point) if any, else the lazily created port session.
    /// Returns null only when construction failed; <see cref="Failure"/> then says why.
    /// </summary>
    internal ModApiSession? ResolveSession(ModApiSession? injected)
    {
        if (injected != null)
            return injected;

        lock (_sync)
        {
            if (_disposed || _failure != null)
                return null;
            if (_session != null)
                return _session;

            try
            {
                // The handler is built once and handed to the client, so a native TLS backend that
                // is missing or unusable fails here, on a controlled path, and not mid-request.
                var handler = _transport.TryCreateHandler();
                if (handler == null)
                {
                    RecordFailure(OnlineSessionFailure.ClientConstructionFailed, null);
                    return null;
                }
                _session = ModApiSession.TryCreate(
                    _apiBaseUrl,
                    _productVersion,
                    "OnlineClient",
                    _transport.RequestTimeout,
                    handler
                );
                if (_session == null)
                {
                    RecordFailure(OnlineSessionFailure.RouteInvalid, null);
                    return null;
                }

                EmitReady();
                return _session;
            }
            catch (Exception ex)
            {
                RecordFailure(OnlineSessionFailure.ClientConstructionFailed, ex);
                return null;
            }
        }
    }

    /// <summary>
    /// BazaarDB link client for the injected-or-port path. Targets a different host than the mod
    /// API, so it is built independently and a mod-API route failure must not disable it.
    /// </summary>
    internal BazaarDbLinkClient? ResolveLinkClient(BazaarDbLinkClient? injected)
    {
        if (injected != null)
            return injected;

        lock (_sync)
        {
            if (_disposed)
                return null;
            if (_linkClient != null)
                return _linkClient;

            try
            {
                // Same handler shape as the mod-API session: on the port the native TLS backend has
                // to be resolved inside one known construction, not lazily on whichever thread sends.
                var handler = _transport.TryCreateHandler();
                if (handler == null)
                {
                    RecordFailure(OnlineSessionFailure.LinkClientConstructionFailed, null);
                    return null;
                }
                var httpClient = BppHttpClientFactory.Create(
                    productVersion: _productVersion,
                    userAgentSuffix: "BazaarDbLink",
                    timeout: TimeSpan.FromSeconds(30),
                    handler: handler
                );
                _linkClient = new BazaarDbLinkClient(
                    httpClient,
                    new Uri(BazaarDbLinkClient.DefaultRedeemEndpoint)
                );
                return _linkClient;
            }
            catch (Exception ex)
            {
                RecordFailure(OnlineSessionFailure.LinkClientConstructionFailed, ex);
                return null;
            }
        }
    }

    /// <summary>Null while healthy; the closed reason once construction has failed.</summary>
    internal OnlineSessionFailure? Failure
    {
        get
        {
            lock (_sync)
                return _failure;
        }
    }

    private void RecordFailure(OnlineSessionFailure failure, Exception? exception)
    {
        _failure = failure;
        var logEvent = new BppLogEvent(
            BppLogFeatureScope.HistoryPanel,
            "history_panel.online.session.unavailable",
            storm: ["reason_code"]
        );
        BppLogField[] fields = [("reason_code", failure)];
        if (exception == null)
            BppLog.WarnEvent(logEvent, fields);
        else
            BppLog.WarnEvent(logEvent, exception, fields);
    }

    private static void EmitReady() =>
        BppLog.DebugEvent(
            new BppLogEvent(BppLogFeatureScope.HistoryPanel, "history_panel.online.session.ready"),
            static () => []
        );

    public void Dispose()
    {
        ModApiSession? session;
        BazaarDbLinkClient? linkClient;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            session = _session;
            linkClient = _linkClient;
            _session = null;
            _linkClient = null;
        }

        // Never let a disposal failure escape into plugin teardown: the remaining teardown steps
        // still have to run.
        try
        {
            session?.Dispose();
        }
        catch (Exception ex)
        {
            BppStartupTrace.Write("online-session-dispose-failed:" + ex.GetType().Name);
        }

        try
        {
            linkClient?.Dispose();
        }
        catch (Exception ex)
        {
            BppStartupTrace.Write("online-link-dispose-failed:" + ex.GetType().Name);
        }

        _transport.Dispose();
    }
}

internal enum OnlineSessionFailure
{
    /// <summary>The configured base URL is not an absolute http(s) URI.</summary>
    RouteInvalid,

    /// <summary>Building the HttpClient or its handler threw (typically a native TLS issue).</summary>
    ClientConstructionFailed,

    /// <summary>Building the BazaarDB link client threw.</summary>
    LinkClientConstructionFailed,
}
