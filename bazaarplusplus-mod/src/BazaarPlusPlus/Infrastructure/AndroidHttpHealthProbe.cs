#if BPP_ANDROID
#nullable enable
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BazaarPlusPlus.Infrastructure;

/// <summary>
/// Explicit, one-shot transport diagnostics only; no session, identity, or protocol parsing.
/// Nothing invokes this probe automatically. Completion does not establish server health.
/// </summary>
internal static class AndroidHttpHealthProbe
{
    // Pinned from ModApiUploadDefaults.ApiBaseUrl and ModApiRoutes.Health, not caller policy.
    private const string AllowedHost = "mod-api-v6.bazaarplusplus.com";
    private const int BodyLimit = 16 * 1024;

    internal enum Observation
    {
        BodyReadCompleted,
        BodyLimitExceeded,
    }

    internal readonly record struct Result(
        Observation Observation,
        int StatusCode,
        int BytesRead
    );

    /// <summary>
    /// Await this method; never use Wait/Result on the Unity thread. Failures and cancellation
    /// propagate to the caller. The deadline includes worker scheduling and client construction;
    /// cancellation is cooperative, not a guarantee against a stuck native DNS/TLS implementation.
    /// </summary>
    internal static async Task<Result> RunAsync(Uri endpoint)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Construction, validation, trace file IO, and disposal also stay off the Unity thread.
        return await Task.Run(() => RunCoreAsync(endpoint, deadline.Token)).ConfigureAwait(false);
    }

    private static async Task<Result> RunCoreAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        int? statusCode = null;
        var bytesRead = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                endpoint is null
                || !endpoint.IsAbsoluteUri
                || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                || !string.Equals(endpoint.IdnHost, AllowedHost, StringComparison.OrdinalIgnoreCase)
                || endpoint.Port != 443
                || endpoint.UserInfo.Length != 0
                || !string.Equals(endpoint.AbsolutePath, "/health", StringComparison.Ordinal)
                || endpoint.Query.Length != 0
                || endpoint.Fragment.Length != 0
            )
            {
                // Never include the rejected URI in an exception or diagnostic field.
                throw new ArgumentException("Only the pinned HTTPS health endpoint is permitted.", nameof(endpoint));
            }

            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                // Disposing an oversized response must not drain more than the read bound.
                MaxResponseDrainSize = 0,
            };
            using var client = new HttpClient(handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            Trace("client_created", statusCode, bytesRead);
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            Trace("request_started", statusCode, bytesRead);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            ).ConfigureAwait(false);
            statusCode = (int)response.StatusCode;
            Trace("headers_received", statusCode, bytesRead);
            cancellationToken.ThrowIfCancellationRequested();

            using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[BodyLimit + 1];
            while (bytesRead < buffer.Length)
            {
                var count = await body.ReadAsync(
                    buffer,
                    bytesRead,
                    buffer.Length - bytesRead,
                    cancellationToken
                ).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (count == 0)
                    break;
                bytesRead += count;
            }

            Trace("body_received", statusCode, bytesRead);
            cancellationToken.ThrowIfCancellationRequested();
            // Includes redirects and non-2xx responses without labelling them successful.
            // The extra byte distinguishes a bounded complete body from an oversized body.
            return new Result(
                bytesRead > BodyLimit ? Observation.BodyLimitExceeded : Observation.BodyReadCompleted,
                statusCode.Value,
                bytesRead
            );
        }
        catch (OperationCanceledException ex)
        {
            Trace("cancelled", statusCode, bytesRead, ex);
            throw;
        }
        catch (Exception ex)
        {
            Trace("failed", statusCode, bytesRead, ex);
            throw;
        }
        finally
        {
            // All using scopes above have unwound, including partial construction failures.
            Trace("disposed", statusCode, bytesRead);
        }
    }

    private static void Trace(string stage, int? statusCode, int bytesRead, Exception? exception = null)
    {
        BppStartupTrace.Write(
            "android_http_health stage=" + stage
            + (statusCode.HasValue ? " status_code=" + statusCode.Value : string.Empty)
            + " bytes=" + bytesRead
            + (exception is null ? string.Empty : " exception_type=" + exception.GetType().Name)
        );
    }
}
#endif
