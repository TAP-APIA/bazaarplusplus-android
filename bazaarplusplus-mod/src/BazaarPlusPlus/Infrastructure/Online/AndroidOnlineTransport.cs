#nullable enable
using System.Net.Http;
using BazaarPlusPlus.Infrastructure.Logging;

namespace BazaarPlusPlus.Infrastructure.Online;

/// <summary>
/// Transport policy for the History module's online calls.
///
/// This is deliberately not a second HTTP client implementation: every request still goes through
/// the ModApi clients so the protocol, DTO parsing and error envelopes stay in one place. What this
/// type owns is the part that has to differ on the Android IL2CPP port:
///
/// 1. Handler shape. The framework default handler resolves a native TLS backend lazily on
///    whichever thread first sends. Building the handler explicitly keeps that resolution inside
///    one known construction and lets a missing or unusable native backend fail as a single
///    diagnosable step instead of a mid-flight abort.
/// 2. Bounded concurrency, so a ghost sync, a replay download and a health probe cannot open three
///    sockets against a metered connection at once.
/// 3. Closed failure classification. Every public member returns a value or a closed reason code;
///    no exception is allowed to cross back into a Unity callback, because an escaping managed
///    exception at the IL2CPP boundary terminates the process.
/// </summary>
internal sealed class AndroidOnlineTransport : IDisposable
{
    /// <summary>Concurrent requests allowed against the mod API.</summary>
    private const int MaxConcurrentRequests = 2;

    /// <summary>
    /// Shortest gap between two starts of the same logical operation. This is a client-side
    /// courtesy limit only; the server's 429 + Retry-After stays authoritative and is honoured by
    /// the callers that already handle it.
    /// </summary>
    private static readonly TimeSpan PerOperationCooldown = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _gate = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private readonly Dictionary<string, long> _nextStartTicksUtc = new(StringComparer.Ordinal);
    private readonly TimeSpan _requestTimeout;
    private int _disposed;

    internal AndroidOnlineTransport(TimeSpan requestTimeout)
    {
        if (requestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _requestTimeout = requestTimeout;
    }

    internal TimeSpan RequestTimeout => _requestTimeout;

    /// <summary>
    /// Builds the handler the ModApi session should use. Android gets explicit connect and pool
    /// limits because the platform default is tuned for a desktop process, not a phone.
    /// Returns null when the platform handler cannot be constructed (typically a native TLS backend
    /// the runtime package did not ship), so the caller degrades one feature instead of throwing
    /// out of a Unity mount callback.
    /// </summary>
    internal HttpMessageHandler? TryCreateHandler()
    {
        try
        {
            return CreateHandler();
        }
        catch (Exception ex)
        {
            BppLog.ErrorEvent(
                new BppLogEvent(
                    BppLogFeatureScope.HistoryPanel,
                    "history_panel.online.handler.failed",
                    storm: []
                ),
                ex,
                ("reason_code", "handler_construction_failed")
            );
            return null;
        }
    }

    private HttpMessageHandler CreateHandler()
    {
#if BPP_ANDROID
        return new SocketsHttpHandler
        {
            // A redirect on a presigned upload/download URL is a protocol change, not something to
            // silently follow: the presigned host is the contract.
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            // Do not hold a stale DNS/TLS session across a long play session on a changing network.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // Disposing an abandoned response must not try to drain the whole body.
            MaxResponseDrainSize = 64 * 1024,
        };
#else
        // Desktop keeps the framework default; the port's transport concerns do not apply.
        return new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
#endif
    }

    /// <summary>
    /// Runs one online operation under the transport's concurrency, cooldown and deadline policy.
    ///
    /// <paramref name="operationBody"/> receives a token that fires on caller cancellation or on
    /// the transport deadline, whichever comes first. The body is responsible for classifying HTTP
    /// outcomes; only unexpected exceptions are converted here, so a caller that wants to tell a
    /// server error from a socket error still can.
    /// </summary>
    internal async Task<OnlineTransportOutcome<T>> RunAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> operationBody,
        CancellationToken cancellationToken,
        TimeSpan? deadline = null
    )
    {
        if (operationBody == null)
            throw new ArgumentNullException(nameof(operationBody));
        if (Volatile.Read(ref _disposed) != 0)
            return OnlineTransportOutcome<T>.Failed(OnlineTransportFailure.Canceled, null);

        if (!TryReserveStart(operation, out var cooldownRemaining))
        {
            EmitThrottled(operation);
            return OnlineTransportOutcome<T>.Failed(
                OnlineTransportFailure.Throttled,
                null,
                retryAfterSeconds: Math.Max(1, (int)Math.Ceiling(cooldownRemaining.TotalSeconds))
            );
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OnlineTransportOutcome<T>.Failed(OnlineTransportFailure.Canceled, null);
        }
        catch (ObjectDisposedException)
        {
            // Unmount disposed the transport between the state check and the wait.
            return OnlineTransportOutcome<T>.Failed(OnlineTransportFailure.Canceled, null);
        }

        try
        {
            // Caller cancellation and the transport deadline are linked so a hung DNS/TLS handshake
            // cannot pin the slot until the HttpClient default of ~100s. A per-operation deadline
            // lets a small interactive action (health, redeem) fail long before a large download.
            var effective = deadline ?? _requestTimeout;
            if (effective <= TimeSpan.Zero)
                effective = _requestTimeout;
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            deadlineCts.CancelAfter(effective);
            try
            {
                var value = await operationBody(deadlineCts.Token).ConfigureAwait(false);
                return OnlineTransportOutcome<T>.Success(value);
            }
            catch (OperationCanceledException ex)
            {
                // A caller-cancelled token is a session boundary (panel closed) and must be
                // discarded silently; a deadline expiry is a real timeout worth surfacing.
                var canceledByCaller = cancellationToken.IsCancellationRequested;
                var failure = canceledByCaller
                    ? OnlineTransportFailure.Canceled
                    : OnlineTransportFailure.Timeout;
                EmitFailure(operation, failure, canceledByCaller ? null : ex);
                return OnlineTransportOutcome<T>.Failed(failure, canceledByCaller ? null : ex);
            }
            catch (Exception ex)
            {
                EmitFailure(operation, OnlineTransportFailure.Faulted, ex);
                return OnlineTransportOutcome<T>.Failed(OnlineTransportFailure.Faulted, ex);
            }
        }
        finally
        {
            // Unmount disposes the gate; an in-flight request that unwinds afterwards must not turn
            // teardown into an ObjectDisposedException on a continuation.
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException) { }
        }
    }

    private bool TryReserveStart(string operation, out TimeSpan cooldownRemaining)
    {
        // Wall-clock ticks, not a monotonic timer: a cooldown set before the device suspended must
        // still have elapsed when it resumes, not restart.
        var nowUtc = DateTimeOffset.UtcNow.UtcTicks;
        lock (_nextStartTicksUtc)
        {
            if (_nextStartTicksUtc.TryGetValue(operation, out var nextUtc) && nowUtc < nextUtc)
            {
                cooldownRemaining = TimeSpan.FromTicks(nextUtc - nowUtc);
                return false;
            }

            _nextStartTicksUtc[operation] = DateTimeOffset
                .UtcNow.Add(PerOperationCooldown)
                .UtcTicks;
            cooldownRemaining = TimeSpan.Zero;
            return true;
        }
    }

    private static void EmitThrottled(string operation) =>
        BppLog.DebugEvent(
            new BppLogEvent(BppLogFeatureScope.HistoryPanel, "history_panel.online.throttled"),
            () => [("operation", operation)]
        );

    private static void EmitFailure(
        string operation,
        OnlineTransportFailure failure,
        Exception? exception
    )
    {
        var logEvent = new BppLogEvent(
            BppLogFeatureScope.HistoryPanel,
            "history_panel.online.transport.failed",
            storm: ["operation", "reason_code"]
        );
        BppLogField[] fields = [("operation", operation), ("reason_code", failure)];
        if (exception == null)
            BppLog.WarnEvent(logEvent, fields);
        else
            BppLog.WarnEvent(logEvent, exception, fields);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _gate.Dispose();
    }
}

/// <summary>Closed set of transport-level outcomes. No raw exception reaches the UI layer.</summary>
internal enum OnlineTransportFailure
{
    /// <summary>The caller's session token fired; discard silently.</summary>
    Canceled,

    /// <summary>The transport deadline elapsed (the server may still be working).</summary>
    Timeout,

    /// <summary>The operation's own cooldown had not elapsed yet.</summary>
    Throttled,

    /// <summary>An unexpected exception, already logged with its type.</summary>
    Faulted,
}

internal readonly struct OnlineTransportOutcome<T>
{
    private OnlineTransportOutcome(
        bool succeeded,
        T? value,
        OnlineTransportFailure? failure,
        Exception? exception,
        int? retryAfterSeconds
    )
    {
        Succeeded = succeeded;
        Value = value;
        Failure = failure;
        Exception = exception;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public bool Succeeded { get; }

    public T? Value { get; }

    public OnlineTransportFailure? Failure { get; }

    public Exception? Exception { get; }

    public int? RetryAfterSeconds { get; }

    /// <summary>True when the caller closed the panel; callers must not surface this.</summary>
    public bool WasCanceled => Failure == OnlineTransportFailure.Canceled;

    public static OnlineTransportOutcome<T> Success(T value) =>
        new(true, value, null, null, null);

    public static OnlineTransportOutcome<T> Failed(
        OnlineTransportFailure failure,
        Exception? exception,
        int? retryAfterSeconds = null
    ) => new(false, default, failure, exception, retryAfterSeconds);
}
