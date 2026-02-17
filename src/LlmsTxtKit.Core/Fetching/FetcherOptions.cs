namespace LlmsTxtKit.Core.Fetching;

/// <summary>
/// Configuration options for <see cref="LlmsTxtFetcher"/>. Controls timeout behavior,
/// retry policy, and the HTTP User-Agent string sent with each request.
/// </summary>
/// <remarks>
/// <para>
/// All properties have sensible defaults. Callers can construct a default instance
/// (<c>new FetcherOptions()</c>) for typical usage and only override specific
/// settings when their deployment environment requires it.
/// </para>
/// <para>
/// These options are passed to the <see cref="LlmsTxtFetcher"/> constructor and
/// apply to all requests made by that fetcher instance. To use different options
/// for different requests, create multiple fetcher instances.
/// </para>
/// <para>
/// See Design Spec § 6 (Configuration Model) for the rationale behind using
/// options objects rather than global settings or static configuration.
/// </para>
/// </remarks>
public sealed class FetcherOptions
{
    /// <summary>
    /// The default User-Agent string identifying LlmsTxtKit.
    /// </summary>
    /// <remarks>
    /// Per SEC-2, the fetcher identifies itself honestly. It does not spoof
    /// browser user agents or attempt to bypass bot detection. The UA string
    /// includes the library name and version so site operators can identify
    /// the traffic source.
    /// </remarks>
    public const string DefaultUserAgent = "LlmsTxtKit/0.2.0 (+https://github.com/southpawriter02/LlmsTxtKit)";

    /// <summary>
    /// The default per-request timeout in seconds.
    /// </summary>
    /// <remarks>
    /// 15 seconds is generous enough for most origin servers while short enough
    /// to avoid blocking the caller indefinitely. Sites behind CDNs typically
    /// respond in under 2 seconds; sites with cold-start serverless backends
    /// may take 5–8 seconds.
    /// </remarks>
    public const int DefaultTimeoutSeconds = 15;

    /// <summary>
    /// The default number of retry attempts for transient failures.
    /// </summary>
    /// <remarks>
    /// Two retries means up to three total attempts (one initial + two retries).
    /// This handles transient network blips without hammering a struggling server.
    /// Only transient failures (5xx responses, timeouts, and connection errors)
    /// are retried. 4xx responses (including 429 Rate Limited) are not retried
    /// automatically — callers handle rate limiting via <see cref="FetchResult.RetryAfter"/>.
    /// </remarks>
    public const int DefaultMaxRetries = 2;

    /// <summary>
    /// The default base delay between retries in milliseconds.
    /// </summary>
    /// <remarks>
    /// The actual delay uses an exponential backoff formula:
    /// <c>delay = RetryDelayMs * 2^attemptIndex</c>. So with the default 1000ms
    /// base: first retry waits ~1s, second retry waits ~2s. A small random jitter
    /// (±10%) is added to prevent thundering-herd effects when many fetcher
    /// instances retry simultaneously.
    /// </remarks>
    public const int DefaultRetryDelayMs = 1000;

    /// <summary>
    /// The User-Agent header sent with each HTTP request.
    /// </summary>
    /// <value>
    /// Defaults to <see cref="DefaultUserAgent"/>.
    /// </value>
    public string UserAgent { get; init; } = DefaultUserAgent;

    /// <summary>
    /// The per-request timeout in seconds. If a request does not complete
    /// within this duration, it is cancelled and the fetcher returns a
    /// <see cref="FetchStatus.Timeout"/> result.
    /// </summary>
    /// <value>
    /// Defaults to <see cref="DefaultTimeoutSeconds"/> (15 seconds).
    /// Must be greater than zero.
    /// </value>
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;

    /// <summary>
    /// The maximum number of retry attempts for transient failures (5xx,
    /// timeouts, connection errors). Set to 0 to disable retries entirely.
    /// </summary>
    /// <value>
    /// Defaults to <see cref="DefaultMaxRetries"/> (2 retries = 3 total attempts).
    /// Must be non-negative.
    /// </value>
    public int MaxRetries { get; init; } = DefaultMaxRetries;

    /// <summary>
    /// The base delay in milliseconds between retry attempts. Actual delay
    /// uses exponential backoff: <c>RetryDelayMs * 2^attemptIndex</c>.
    /// </summary>
    /// <value>
    /// Defaults to <see cref="DefaultRetryDelayMs"/> (1000ms).
    /// Must be non-negative.
    /// </value>
    public int RetryDelayMs { get; init; } = DefaultRetryDelayMs;

    /// <summary>
    /// An optional override for the HTTP <c>Accept</c> header. When <c>null</c>,
    /// the fetcher sends <c>Accept: text/plain, text/markdown</c> which aligns
    /// with the expected content types for llms.txt files.
    /// </summary>
    /// <value>
    /// Defaults to <c>null</c> (uses the standard Accept header).
    /// </value>
    public string? AcceptHeaderOverride { get; init; }

    /// <summary>
    /// The maximum allowed response body size in bytes. Responses larger than
    /// this are truncated to prevent memory exhaustion from unexpectedly large
    /// "llms.txt" files that are clearly not legitimate.
    /// </summary>
    /// <value>
    /// Defaults to 5 MB. Per SEC-3 (input validation), extremely large inputs
    /// are rejected to prevent denial-of-service via memory exhaustion.
    /// </value>
    public long MaxResponseSizeBytes { get; init; } = 5 * 1024 * 1024; // 5 MB
}
