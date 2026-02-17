using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using LlmsTxtKit.Core.Parsing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmsTxtKit.Core.Fetching;

/// <summary>
/// Fetches llms.txt files from the open web with infrastructure-aware error handling.
/// This is the primary entry point for retrieving llms.txt files by domain name.
/// </summary>
/// <remarks>
/// <para>
/// The fetcher constructs the URL as <c>https://{domain}/llms.txt</c> and performs
/// an HTTP GET request with configurable timeouts, retries, and User-Agent string.
/// The response is classified into one of seven <see cref="FetchStatus"/> categories,
/// giving callers structured information without exception-based control flow.
/// </para>
/// <para>
/// <strong>WAF block detection:</strong> When a 403 response is received, the fetcher
/// inspects response headers for signatures from known WAF vendors (Cloudflare, AWS WAF,
/// Akamai, etc.). If a WAF is identified, <see cref="FetchResult.BlockReason"/> is
/// populated with a human-readable diagnosis. This is best-effort — not all WAFs produce
/// identifiable responses. See Design Spec § 2.2.
/// </para>
/// <para>
/// <strong>Retry policy:</strong> Transient failures (5xx responses, timeouts, and
/// connection errors) are retried with exponential backoff up to
/// <see cref="FetcherOptions.MaxRetries"/> times. Non-transient failures (4xx responses)
/// are not retried. Rate-limited responses (429) are returned immediately with
/// <see cref="FetchResult.RetryAfter"/> populated so callers can implement their own
/// backoff strategy.
/// </para>
/// <para>
/// <strong>HttpClient lifecycle:</strong> The fetcher accepts an optional
/// <see cref="HttpClient"/> via constructor injection for proper lifecycle management
/// with <c>IHttpClientFactory</c>. If no client is provided, an internal client
/// is created with default settings. See Design Spec § 2.2.
/// </para>
/// </remarks>
public sealed class LlmsTxtFetcher : IDisposable
{
    // -- Private fields --

    /// <summary>The HTTP client used for all requests.</summary>
    private readonly HttpClient _httpClient;

    /// <summary>Configuration for this fetcher instance.</summary>
    private readonly FetcherOptions _options;

    /// <summary>Logger for diagnostic output at all decision points.</summary>
    private readonly ILogger<LlmsTxtFetcher> _logger;

    /// <summary>
    /// Whether this instance owns the HttpClient (i.e., created it internally)
    /// and should dispose it. When an external client is injected, we don't dispose it.
    /// </summary>
    private readonly bool _ownsHttpClient;

    /// <summary>Whether this instance has been disposed.</summary>
    private bool _disposed;

    // -- Constructor --

    /// <summary>
    /// Creates a new <see cref="LlmsTxtFetcher"/> with the specified options
    /// and optional HTTP client.
    /// </summary>
    /// <param name="options">
    /// Configuration for timeouts, retries, and user-agent. If <c>null</c>,
    /// default options are used.
    /// </param>
    /// <param name="httpClient">
    /// Optional <see cref="HttpClient"/> instance. If <c>null</c>, an internal
    /// client is created with default settings. Prefer supplying a client from
    /// <c>IHttpClientFactory</c> for proper connection lifecycle management
    /// in long-running applications.
    /// </param>
    /// <param name="logger">
    /// Optional logger for diagnostic output. If <c>null</c>, a no-op logger
    /// is used. When provided, the fetcher logs at every decision point:
    /// request URLs, timeouts, retry attempts, WAF detection results, DNS
    /// failures, response sizes, and classification outcomes.
    /// </param>
    public LlmsTxtFetcher(FetcherOptions? options = null, HttpClient? httpClient = null, ILogger<LlmsTxtFetcher>? logger = null)
    {
        _options = options ?? new FetcherOptions();
        _logger = logger ?? NullLogger<LlmsTxtFetcher>.Instance;

        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            // Create an internal HttpClient with a SocketsHttpHandler for
            // connection pooling. The timeout is NOT set on the HttpClient itself —
            // we use per-request CancellationTokenSource timeouts instead, which
            // gives us more granular control and avoids the "HttpClient.Timeout
            // is shared across all requests" problem.
            _httpClient = new HttpClient(new SocketsHttpHandler
            {
                // Allow automatic decompression for smaller responses
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                // Pool connections for reuse across multiple fetch calls
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });
            _ownsHttpClient = true;
        }

        // Set the default request timeout to Infinite — we manage timeouts
        // per-request via CancellationTokenSource in FetchAsync.
        _httpClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
    }

    // -- Public API --

    /// <summary>
    /// Fetches and parses the llms.txt file for the specified domain.
    /// </summary>
    /// <param name="domain">
    /// The domain to fetch from. The fetcher constructs the URL as
    /// <c>https://{domain}/llms.txt</c>. Must not be null or empty.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token for async cancellation. When cancelled, the method
    /// throws <see cref="OperationCanceledException"/>.
    /// </param>
    /// <returns>
    /// A <see cref="FetchResult"/> describing the outcome. The result's
    /// <see cref="FetchResult.Status"/> property indicates what happened;
    /// other properties are populated based on the status.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="domain"/> is null, empty, or whitespace.
    /// This is a programming error, not a runtime condition.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// Thrown if this fetcher instance has been disposed. Unrecoverable.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken"/> is cancelled.
    /// </exception>
    public async Task<FetchResult> FetchAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        // -- Input validation (programming errors throw, per Design Spec § 5) --
        ArgumentException.ThrowIfNullOrWhiteSpace(domain, nameof(domain));
        ObjectDisposedException.ThrowIf(_disposed, this);

        // -- Build the target URL --
        // Per the spec, llms.txt lives at the root path of the domain.
        var url = $"https://{domain}/llms.txt";
        _logger.LogDebug("Fetching llms.txt for domain={Domain}, url={Url}, timeout={Timeout}s, maxRetries={MaxRetries}",
            domain, url, _options.TimeoutSeconds, _options.MaxRetries);

        // -- Start the stopwatch for Duration tracking --
        var stopwatch = Stopwatch.StartNew();

        // -- Attempt the fetch with retry logic --
        // We retry transient failures (5xx, timeouts, connection errors) up to
        // MaxRetries times. Non-transient failures (4xx) are returned immediately.
        int maxAttempts = _options.MaxRetries + 1;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            // If this is a retry, wait with exponential backoff before trying again.
            // Delay formula: baseDelay * 2^attempt, with ±10% jitter.
            if (attempt > 0)
            {
                int baseDelayMs = _options.RetryDelayMs * (1 << (attempt - 1));
                int jitterMs = Random.Shared.Next(
                    -(baseDelayMs / 10),
                    (baseDelayMs / 10) + 1);
                int delayMs = Math.Max(0, baseDelayMs + jitterMs);

                _logger.LogDebug("Retry attempt {Attempt}/{MaxAttempts} for domain={Domain} after {Delay}ms backoff",
                    attempt + 1, maxAttempts, domain, delayMs);
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

            var result = await ExecuteSingleFetchAsync(
                url, domain, stopwatch, cancellationToken).ConfigureAwait(false);

            // If the result is non-transient, return it immediately (no retry).
            // Transient statuses that warrant retry: Timeout, Error (5xx or connection).
            // Non-transient statuses returned immediately: Success, NotFound, Blocked,
            // RateLimited, DnsFailure, Error (4xx).
            bool isTransient = result.Status switch
            {
                FetchStatus.Timeout => true,
                // 5xx errors are transient; 4xx errors are not
                FetchStatus.Error when result.StatusCode is >= 500 or null => true,
                _ => false
            };

            // Return immediately if:
            // 1. The result is non-transient (no point retrying a 404 or a WAF block)
            // 2. This was the last attempt (no more retries available)
            if (!isTransient || attempt == maxAttempts - 1)
            {
                if (result.Status == FetchStatus.Success)
                    _logger.LogInformation("Fetch succeeded for domain={Domain}, status={StatusCode}, duration={Duration}ms, bodySize={BodySize}",
                        domain, result.StatusCode, result.Duration.TotalMilliseconds, result.RawContent?.Length ?? 0);
                else if (isTransient && attempt == maxAttempts - 1)
                    _logger.LogWarning("All {MaxAttempts} attempts exhausted for domain={Domain}, final status={Status}, error={Error}",
                        maxAttempts, domain, result.Status, result.ErrorMessage);
                else
                    _logger.LogDebug("Non-transient result for domain={Domain}, status={Status}, statusCode={StatusCode}",
                        domain, result.Status, result.StatusCode);

                return result;
            }

            _logger.LogDebug("Transient failure on attempt {Attempt} for domain={Domain}, status={Status} — will retry",
                attempt + 1, domain, result.Status);
        }

        // This should be unreachable — the loop always returns on the last attempt.
        // But the compiler needs a return statement, so we return a generic error.
        stopwatch.Stop();
        return new FetchResult
        {
            Status = FetchStatus.Error,
            Domain = domain,
            Duration = stopwatch.Elapsed,
            ErrorMessage = "Unexpected: all retry attempts exhausted without producing a result."
        };
    }

    // -- Private methods --

    /// <summary>
    /// Executes a single HTTP GET request and classifies the result.
    /// Does NOT handle retries — that's the caller's responsibility.
    /// </summary>
    private async Task<FetchResult> ExecuteSingleFetchAsync(
        string url,
        string domain,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        try
        {
            // Build the HTTP request with configured headers
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
            request.Headers.Accept.ParseAdd(
                _options.AcceptHeaderOverride ?? "text/plain, text/markdown");

            // Create a per-request timeout using a linked CancellationTokenSource.
            // This gives us per-request timeout control independent of HttpClient.Timeout.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            // Send the request
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            // Read the response body with size limit enforcement (SEC-3)
            string? responseBody = await ReadResponseBodyAsync(
                response, timeoutCts.Token).ConfigureAwait(false);

            // Extract response headers into a flat dictionary for the result
            var headers = ExtractResponseHeaders(response);

            // Classify the response based on HTTP status code and headers
            return ClassifyResponse(
                response, responseBody, headers, domain, stopwatch);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The per-request timeout fired, not the caller's cancellation token.
            // This is a Timeout result, not a cancellation.
            stopwatch.Stop();
            _logger.LogWarning("Request timed out for domain={Domain} after {Timeout}s (elapsed={Elapsed}ms)",
                domain, _options.TimeoutSeconds, stopwatch.ElapsedMilliseconds);
            return new FetchResult
            {
                Status = FetchStatus.Timeout,
                Domain = domain,
                Duration = stopwatch.Elapsed,
                ErrorMessage = $"Request timed out after {_options.TimeoutSeconds} seconds."
            };
        }
        catch (OperationCanceledException)
        {
            // The caller's cancellation token was triggered. Propagate as-is.
            _logger.LogDebug("Fetch cancelled by caller for domain={Domain}", domain);
            throw;
        }
        catch (HttpRequestException ex) when (IsDnsFailure(ex))
        {
            // DNS resolution failed — the domain doesn't exist or DNS is broken.
            stopwatch.Stop();
            _logger.LogWarning("DNS resolution failed for domain={Domain}: {Error}", domain, ex.Message);
            return new FetchResult
            {
                Status = FetchStatus.DnsFailure,
                Domain = domain,
                Duration = stopwatch.Elapsed,
                ErrorMessage = $"DNS resolution failed for \"{domain}\": {ex.Message}"
            };
        }
        catch (HttpRequestException ex)
        {
            // Other HTTP-level failures: TLS errors, connection resets, etc.
            stopwatch.Stop();
            _logger.LogWarning("HTTP request failed for domain={Domain}, statusCode={StatusCode}: {Error}",
                domain, (int?)ex.StatusCode, ex.Message);
            return new FetchResult
            {
                Status = FetchStatus.Error,
                Domain = domain,
                Duration = stopwatch.Elapsed,
                StatusCode = (int?)ex.StatusCode,
                ErrorMessage = $"HTTP request failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Reads the HTTP response body as a string, enforcing the configured
    /// maximum response size to prevent memory exhaustion (SEC-3).
    /// </summary>
    /// <param name="response">The HTTP response to read from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response body as a string, or <c>null</c> if the body is empty.</returns>
    private async Task<string?> ReadResponseBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        // Check Content-Length header first for a quick rejection
        if (response.Content.Headers.ContentLength > _options.MaxResponseSizeBytes)
        {
            _logger.LogWarning("Response body too large: Content-Length={ContentLength} exceeds limit={Limit} bytes",
                response.Content.Headers.ContentLength, _options.MaxResponseSizeBytes);
            return null; // Body too large — don't even read it
        }

        // Read the body with a size limit. We read into a MemoryStream first
        // to enforce the limit before converting to string.
        using var contentStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using var limitedStream = new MemoryStream();
        var buffer = new byte[8192];
        long totalRead = 0;

        while (true)
        {
            int bytesRead = await contentStream
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead == 0)
                break;

            totalRead += bytesRead;
            if (totalRead > _options.MaxResponseSizeBytes)
            {
                // Response exceeds size limit — return what we have so far.
                // The caller will still get a partial body for diagnostic purposes.
                _logger.LogWarning("Response body exceeded size limit during streaming: read={TotalRead} > limit={Limit} bytes",
                    totalRead, _options.MaxResponseSizeBytes);
                break;
            }

            limitedStream.Write(buffer, 0, bytesRead);
        }

        if (limitedStream.Length == 0)
            return null;

        limitedStream.Position = 0;
        using var reader = new StreamReader(limitedStream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts selected response headers into a flat dictionary with lowercase
    /// header names for consistent lookup (HTTP headers are case-insensitive
    /// per RFC 7230 § 3.2).
    /// </summary>
    /// <param name="response">The HTTP response to extract headers from.</param>
    /// <returns>A dictionary of lowercase header names to their values.</returns>
    private static Dictionary<string, string> ExtractResponseHeaders(
        HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Capture response headers (transport-level: server, cf-ray, etc.)
        foreach (var header in response.Headers)
        {
            headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
        }

        // Capture content headers (etag, last-modified, content-type, etc.)
        foreach (var header in response.Content.Headers)
        {
            headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
        }

        return headers;
    }

    /// <summary>
    /// Classifies an HTTP response into a <see cref="FetchResult"/> based on
    /// the status code, response headers, and response body.
    /// </summary>
    /// <remarks>
    /// Classification priority:
    /// <list type="number">
    /// <item>200 OK → Success (parse the body)</item>
    /// <item>404 Not Found → NotFound</item>
    /// <item>429 Too Many Requests → RateLimited (parse Retry-After)</item>
    /// <item>403 Forbidden → Blocked (attempt WAF vendor identification)</item>
    /// <item>5xx → Error (transient, eligible for retry)</item>
    /// <item>Other 4xx → Error (non-transient)</item>
    /// </list>
    /// </remarks>
    private FetchResult ClassifyResponse(
        HttpResponseMessage response,
        string? responseBody,
        Dictionary<string, string> headers,
        string domain,
        Stopwatch stopwatch)
    {
        int statusCode = (int)response.StatusCode;

        // -- 200 OK: Success --
        if (response.IsSuccessStatusCode)
        {
            stopwatch.Stop();

            // Parse the response body into an LlmsDocument
            LlmsDocument? document = null;
            if (responseBody is not null)
            {
                document = LlmsDocumentParser.Parse(responseBody);
            }

            return new FetchResult
            {
                Status = FetchStatus.Success,
                Domain = domain,
                Duration = stopwatch.Elapsed,
                Document = document,
                RawContent = responseBody,
                StatusCode = statusCode,
                ResponseHeaders = headers
            };
        }

        // -- 404 Not Found --
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            stopwatch.Stop();
            return new FetchResult
            {
                Status = FetchStatus.NotFound,
                Domain = domain,
                Duration = stopwatch.Elapsed,
                StatusCode = statusCode,
                ResponseHeaders = headers,
                ErrorMessage = $"No llms.txt file found at https://{domain}/llms.txt (HTTP 404)."
            };
        }

        // -- 429 Too Many Requests: Rate Limited --
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            stopwatch.Stop();
            return new FetchResult
            {
                Status = FetchStatus.RateLimited,
                Domain = domain,
                Duration = stopwatch.Elapsed,
                StatusCode = statusCode,
                ResponseHeaders = headers,
                RetryAfter = ParseRetryAfterHeader(response),
                ErrorMessage = $"Rate limited by {domain} (HTTP 429)."
            };
        }

        // -- 403 Forbidden: Check for WAF block --
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            stopwatch.Stop();
            string? blockReason = IdentifyWafBlock(headers, responseBody);
            _logger.LogWarning("HTTP 403 Forbidden for domain={Domain}, wafDetected={WafDetected}, blockReason={BlockReason}",
                domain, blockReason != null, blockReason ?? "WAF vendor could not be identified");

            return new FetchResult
            {
                Status = FetchStatus.Blocked,
                Domain = domain,
                Duration = stopwatch.Elapsed,
                StatusCode = statusCode,
                ResponseHeaders = headers,
                RawContent = responseBody,
                BlockReason = blockReason ?? "Request blocked (HTTP 403 Forbidden). WAF vendor could not be identified."
            };
        }

        // -- 503 Service Unavailable: Could be WAF challenge or genuine downtime --
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            string? blockReason = IdentifyWafBlock(headers, responseBody);

            stopwatch.Stop();

            // If WAF signatures detected, classify as Blocked rather than Error
            if (blockReason is not null)
            {
                return new FetchResult
                {
                    Status = FetchStatus.Blocked,
                    Domain = domain,
                    Duration = stopwatch.Elapsed,
                    StatusCode = statusCode,
                    ResponseHeaders = headers,
                    RawContent = responseBody,
                    BlockReason = blockReason
                };
            }

            // Otherwise it's a transient server error
            return new FetchResult
            {
                Status = FetchStatus.Error,
                Domain = domain,
                Duration = stopwatch.Elapsed,
                StatusCode = statusCode,
                ResponseHeaders = headers,
                ErrorMessage = $"Server returned 503 Service Unavailable for https://{domain}/llms.txt."
            };
        }

        // -- Other status codes: generic Error --
        stopwatch.Stop();
        return new FetchResult
        {
            Status = FetchStatus.Error,
            Domain = domain,
            Duration = stopwatch.Elapsed,
            StatusCode = statusCode,
            ResponseHeaders = headers,
            RawContent = responseBody,
            ErrorMessage = $"Unexpected HTTP {statusCode} from https://{domain}/llms.txt."
        };
    }

    // -- WAF Detection Heuristics (Design Spec § 2.2) --

    /// <summary>
    /// Attempts to identify the WAF vendor responsible for blocking a request
    /// by inspecting response headers and the response body for known signatures.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <em>best-effort heuristic detection</em>. WAF vendors change their
    /// response signatures over time, and some responses are indistinguishable from
    /// generic web server responses. A <c>null</c> return means the WAF could not
    /// be identified, not that no WAF is present.
    /// </para>
    /// <para>
    /// Currently detects: Cloudflare, AWS WAF/CloudFront, Akamai.
    /// Per SEC-2, the fetcher does not attempt to circumvent these blocks.
    /// </para>
    /// </remarks>
    /// <param name="headers">The lowercase-keyed response headers.</param>
    /// <param name="responseBody">The response body, or <c>null</c>.</param>
    /// <returns>A human-readable block reason, or <c>null</c> if no WAF identified.</returns>
    private static string? IdentifyWafBlock(
        Dictionary<string, string> headers,
        string? responseBody)
    {
        // -- Cloudflare detection --
        // Cloudflare responses typically include a "cf-ray" header (a unique
        // request ID) and may include "cf-mitigated" when a request is actively
        // blocked. The "server" header is often "cloudflare".
        if (headers.TryGetValue("cf-ray", out _))
        {
            return "Blocked by Cloudflare WAF (cf-ray header detected). "
                   + "The site's bot protection is preventing automated access.";
        }

        if (headers.TryGetValue("server", out var serverValue))
        {
            if (serverValue.Contains("cloudflare", StringComparison.OrdinalIgnoreCase))
            {
                return "Blocked by Cloudflare WAF (server: cloudflare header detected). "
                       + "The site's bot protection is preventing automated access.";
            }

            // -- AWS WAF / CloudFront detection --
            // AWS CloudFront responses include "server: CloudFront" or "server: AmazonS3".
            // AWS WAF blocks often include an "x-amzn-waf-action" header.
            if (serverValue.Contains("CloudFront", StringComparison.OrdinalIgnoreCase))
            {
                return "Blocked by AWS CloudFront/WAF (server: CloudFront header detected). "
                       + "The site's bot protection is preventing automated access.";
            }

            // -- Akamai detection --
            // Akamai responses often include "server: AkamaiGHost" or
            // "x-akamai-transformed" headers.
            if (serverValue.Contains("AkamaiGHost", StringComparison.OrdinalIgnoreCase))
            {
                return "Blocked by Akamai WAF (server: AkamaiGHost header detected). "
                       + "The site's bot protection is preventing automated access.";
            }
        }

        // -- AWS WAF header-based detection --
        if (headers.ContainsKey("x-amzn-waf-action"))
        {
            return "Blocked by AWS WAF (x-amzn-waf-action header detected). "
                   + "The site's bot protection is preventing automated access.";
        }

        // -- Akamai header-based detection --
        if (headers.ContainsKey("x-akamai-transformed"))
        {
            return "Blocked by Akamai WAF (x-akamai-transformed header detected). "
                   + "The site's bot protection is preventing automated access.";
        }

        // -- Response body heuristics (less reliable, but sometimes useful) --
        if (responseBody is not null)
        {
            // Cloudflare challenge pages contain distinctive markup
            if (responseBody.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase)
                || responseBody.Contains("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase))
            {
                return "Blocked by Cloudflare WAF (challenge page detected in response body). "
                       + "The site requires browser verification that automated clients cannot complete.";
            }
        }

        // No WAF vendor identified
        return null;
    }

    /// <summary>
    /// Parses the <c>Retry-After</c> header from an HTTP response into a
    /// <see cref="TimeSpan"/>. Supports both delta-seconds and HTTP-date formats.
    /// </summary>
    /// <param name="response">The HTTP response to extract the header from.</param>
    /// <returns>
    /// A <see cref="TimeSpan"/> representing the recommended wait duration,
    /// or <c>null</c> if the header is not present or cannot be parsed.
    /// </returns>
    private static TimeSpan? ParseRetryAfterHeader(HttpResponseMessage response)
    {
        // RetryAfter header is accessible via the typed headers API
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
            return null;

        // Format 1: delta-seconds (e.g., "Retry-After: 120")
        if (retryAfter.Delta.HasValue)
        {
            return retryAfter.Delta.Value;
        }

        // Format 2: HTTP-date (e.g., "Retry-After: Wed, 21 Oct 2015 07:28:00 GMT")
        if (retryAfter.Date.HasValue)
        {
            var waitDuration = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            // If the date is in the past, return zero (don't wait)
            return waitDuration > TimeSpan.Zero ? waitDuration : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// Determines whether an <see cref="HttpRequestException"/> was caused by
    /// a DNS resolution failure, as opposed to other connection-level issues.
    /// </summary>
    /// <param name="ex">The exception to inspect.</param>
    /// <returns><c>true</c> if the exception indicates a DNS failure.</returns>
    private static bool IsDnsFailure(HttpRequestException ex)
    {
        // .NET wraps DNS failures in HttpRequestException with a SocketException
        // inner exception whose SocketErrorCode is HostNotFound or TryAgain.
        if (ex.InnerException is SocketException socketEx)
        {
            return socketEx.SocketErrorCode is
                SocketError.HostNotFound or
                SocketError.NoData or
                SocketError.TryAgain;
        }

        // Fallback: check the message for common DNS failure patterns.
        // This is less reliable but catches some edge cases.
        if (ex.Message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("nodename nor servname provided", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    // -- IDisposable --

    /// <summary>
    /// Disposes the internal <see cref="HttpClient"/> if it was created by this instance.
    /// Externally-injected clients are not disposed (the caller owns their lifecycle).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        _disposed = true;
    }
}
