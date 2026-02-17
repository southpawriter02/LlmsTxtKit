using LlmsTxtKit.Core.Parsing;

namespace LlmsTxtKit.Core.Fetching;

/// <summary>
/// Represents the structured outcome of an HTTP fetch attempt for an llms.txt file.
/// This is the primary return type of <see cref="LlmsTxtFetcher.FetchAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FetchResult"/> uses a structured-result pattern rather than exceptions.
/// A 403 from Cloudflare, a 404, or a DNS failure is a <em>common, expected outcome</em>
/// when fetching llms.txt files across the open web — not an exceptional condition.
/// Callers can match on <see cref="Status"/> to determine what happened without
/// try/catch boilerplate.
/// </para>
/// <para>
/// Depending on the <see cref="Status"/> value, different properties are populated:
/// </para>
/// <list type="bullet">
/// <item><see cref="FetchStatus.Success"/>: <see cref="Document"/> and
///   <see cref="RawContent"/> are populated.</item>
/// <item><see cref="FetchStatus.Blocked"/>: <see cref="BlockReason"/> may contain
///   a WAF vendor diagnosis.</item>
/// <item><see cref="FetchStatus.RateLimited"/>: <see cref="RetryAfter"/> may
///   contain the parsed <c>Retry-After</c> header.</item>
/// <item><see cref="FetchStatus.DnsFailure"/>, <see cref="FetchStatus.Timeout"/>,
///   <see cref="FetchStatus.Error"/>: <see cref="ErrorMessage"/> contains a
///   human-readable description.</item>
/// </list>
/// <para>
/// <see cref="Duration"/> is always populated on all outcomes for performance monitoring.
/// </para>
/// </remarks>
public sealed class FetchResult
{
    /// <summary>
    /// The classified outcome of the fetch attempt.
    /// </summary>
    public required FetchStatus Status { get; init; }

    /// <summary>
    /// The parsed <see cref="LlmsDocument"/>, or <c>null</c> if the fetch did not
    /// succeed. Populated only when <see cref="Status"/> is <see cref="FetchStatus.Success"/>.
    /// </summary>
    public LlmsDocument? Document { get; init; }

    /// <summary>
    /// The raw HTTP response body as a string, or <c>null</c> if the response body
    /// was not available (e.g., DNS failure, timeout). Populated on
    /// <see cref="FetchStatus.Success"/> and sometimes on error statuses where a
    /// response body was received (e.g., WAF challenge pages).
    /// </summary>
    public string? RawContent { get; init; }

    /// <summary>
    /// The HTTP status code from the response, or <c>null</c> if no HTTP response
    /// was received (e.g., DNS failure, timeout, connection error).
    /// </summary>
    public int? StatusCode { get; init; }

    /// <summary>
    /// Selected HTTP response headers, or <c>null</c> if no HTTP response was received.
    /// Includes headers relevant to caching (<c>ETag</c>, <c>Last-Modified</c>,
    /// <c>Cache-Control</c>) and WAF detection (<c>cf-ray</c>, <c>server</c>).
    /// </summary>
    /// <remarks>
    /// Header names are stored in lowercase for consistent lookup (HTTP headers
    /// are case-insensitive per RFC 7230 § 3.2). Values preserve their original casing.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? ResponseHeaders { get; init; }

    /// <summary>
    /// A human-readable description of why the request was blocked, or <c>null</c>
    /// if the request was not blocked. Populated when <see cref="Status"/> is
    /// <see cref="FetchStatus.Blocked"/>.
    /// </summary>
    /// <remarks>
    /// The fetcher uses response header heuristics to identify the blocking WAF vendor
    /// (e.g., "Blocked by Cloudflare WAF (cf-ray header detected)"). This is best-effort —
    /// not all WAFs produce identifiable responses. See Design Spec § 2.2 for the
    /// detection heuristics.
    /// </remarks>
    public string? BlockReason { get; init; }

    /// <summary>
    /// The parsed <c>Retry-After</c> header value, or <c>null</c> if not applicable.
    /// Populated when <see cref="Status"/> is <see cref="FetchStatus.RateLimited"/>
    /// and the response included a <c>Retry-After</c> header.
    /// </summary>
    /// <remarks>
    /// The <c>Retry-After</c> header can be either a number of seconds or an HTTP-date.
    /// Both formats are parsed into a <see cref="TimeSpan"/> representing the
    /// recommended wait duration.
    /// </remarks>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// A human-readable description of the error, or <c>null</c> if no error occurred.
    /// Populated when <see cref="Status"/> is <see cref="FetchStatus.DnsFailure"/>,
    /// <see cref="FetchStatus.Timeout"/>, or <see cref="FetchStatus.Error"/>.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// How long the fetch attempt took, from request initiation to result classification.
    /// Always populated on all outcomes for performance monitoring and diagnostics.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// The domain that was fetched. Useful for logging and cache keying.
    /// </summary>
    public required string Domain { get; init; }
}
