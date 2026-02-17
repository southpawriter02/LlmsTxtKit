namespace LlmsTxtKit.Core.Fetching;

/// <summary>
/// Classifies the outcome of an HTTP fetch attempt for an llms.txt file.
/// </summary>
/// <remarks>
/// <para>
/// The fetcher never throws on non-success HTTP responses. Instead, it returns a
/// <see cref="FetchResult"/> whose <see cref="FetchResult.Status"/> property is one
/// of these seven enum values. This gives callers structured information they can
/// match on without try/catch boilerplate.
/// </para>
/// <para>
/// The seven categories are intentionally coarse-grained. They represent the
/// distinct *decisions* a caller needs to make ("retry later", "don't bother",
/// "investigate WAF block"), not every possible HTTP status code. Callers who
/// need the raw status code can inspect <see cref="FetchResult.StatusCode"/>.
/// </para>
/// </remarks>
public enum FetchStatus
{
    /// <summary>
    /// The fetch succeeded and the response body was parsed into an
    /// <see cref="Parsing.LlmsDocument"/>. HTTP 200 OK.
    /// </summary>
    Success,

    /// <summary>
    /// The server responded with 404 Not Found. The domain does not appear
    /// to have an llms.txt file at the standard <c>/llms.txt</c> path.
    /// </summary>
    NotFound,

    /// <summary>
    /// The request was blocked by a Web Application Firewall (WAF) or
    /// bot-detection system. Typically HTTP 403 Forbidden, but may also
    /// manifest as 503 with a challenge page.
    /// </summary>
    /// <remarks>
    /// When this status is returned, <see cref="FetchResult.BlockReason"/>
    /// may contain a human-readable diagnosis identifying the WAF vendor
    /// (e.g., Cloudflare, AWS WAF) based on response header heuristics.
    /// See Design Spec § 2.2 for details on WAF detection logic.
    /// </remarks>
    Blocked,

    /// <summary>
    /// The server responded with HTTP 429 Too Many Requests, indicating
    /// rate limiting is in effect.
    /// </summary>
    /// <remarks>
    /// When this status is returned, <see cref="FetchResult.RetryAfter"/>
    /// may contain the parsed <c>Retry-After</c> header value as a
    /// <see cref="TimeSpan"/>, indicating how long the caller should
    /// wait before retrying.
    /// </remarks>
    RateLimited,

    /// <summary>
    /// DNS resolution failed for the target domain. The domain may not
    /// exist, or there may be a transient DNS infrastructure issue.
    /// </summary>
    DnsFailure,

    /// <summary>
    /// The HTTP request exceeded the configured timeout without receiving
    /// a complete response. The server may be slow, unreachable, or the
    /// configured <see cref="FetcherOptions.TimeoutSeconds"/> may be
    /// too aggressive.
    /// </summary>
    Timeout,

    /// <summary>
    /// A catch-all for failures that don't fit the other categories. This
    /// includes unexpected HTTP status codes (5xx, unexpected 4xx), TLS
    /// handshake failures, connection resets, and other transport-level errors.
    /// </summary>
    /// <remarks>
    /// <see cref="FetchResult.ErrorMessage"/> will contain a human-readable
    /// description of the specific failure. <see cref="FetchResult.StatusCode"/>
    /// may be populated if the failure occurred after receiving an HTTP response.
    /// </remarks>
    Error
}
