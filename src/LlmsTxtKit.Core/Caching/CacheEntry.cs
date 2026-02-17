using LlmsTxtKit.Core.Fetching;
using LlmsTxtKit.Core.Parsing;
using LlmsTxtKit.Core.Validation;

namespace LlmsTxtKit.Core.Caching;

/// <summary>
/// Represents a single cached item in the <see cref="LlmsTxtCache"/>. Each entry
/// stores a parsed <see cref="LlmsDocument"/> along with metadata about when it
/// was fetched, when it expires, and the full <see cref="FetchResult"/> that
/// produced it.
/// </summary>
/// <remarks>
/// <para>
/// Cache entries are immutable once created. The <see cref="LlmsTxtCache"/> creates
/// new entries on each fetch rather than mutating existing ones. This makes the cache
/// safe for concurrent readers without locks on the entry level.
/// </para>
/// <para>
/// The <see cref="ValidationReport"/> property is nullable because validation is
/// optional — a document may be cached immediately after fetching, before any
/// validation has run. Callers that need validated data should check this property
/// and trigger validation if it's null.
/// </para>
/// <para>
/// The <see cref="HttpHeaders"/> dictionary stores ETag, Last-Modified, and other
/// headers relevant to conditional requests (RFC 7232). These are preserved so that
/// future re-fetch attempts can send <c>If-None-Match</c> or <c>If-Modified-Since</c>
/// headers to reduce bandwidth.
/// </para>
/// </remarks>
public sealed class CacheEntry
{
    /// <summary>
    /// The parsed llms.txt document. This is the primary payload of the cache entry.
    /// </summary>
    public required LlmsDocument Document { get; init; }

    /// <summary>
    /// The most recent validation report for this document, or <c>null</c> if
    /// the document has not been validated since it was cached. Consumers that
    /// require validated data should check this property.
    /// </summary>
    public ValidationReport? ValidationReport { get; init; }

    /// <summary>
    /// When the content was last fetched from the origin server. Used in
    /// conjunction with <see cref="ExpiresAt"/> to determine freshness.
    /// </summary>
    public required DateTimeOffset FetchedAt { get; init; }

    /// <summary>
    /// When this cache entry's TTL expires. After this time, the entry is
    /// considered stale. Stale entries may still be returned if
    /// <see cref="CacheOptions.StaleWhileRevalidate"/> is enabled.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// HTTP response headers relevant to caching and conditional requests.
    /// Typically includes <c>ETag</c>, <c>Last-Modified</c>, and <c>Cache-Control</c>.
    /// May be <c>null</c> if no relevant headers were present in the response.
    /// </summary>
    /// <remarks>
    /// Header names are stored in lowercase for consistent lookup, following
    /// the convention established by <see cref="FetchResult.ResponseHeaders"/>.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? HttpHeaders { get; init; }

    /// <summary>
    /// The full fetch result that produced this cache entry. Retained so that
    /// callers can inspect fetch metadata (status code, duration, WAF info)
    /// without re-fetching.
    /// </summary>
    public required FetchResult FetchResult { get; init; }

    /// <summary>
    /// The last time this entry was accessed (read from the cache). Used by
    /// the LRU eviction algorithm to determine which entries to evict when
    /// <see cref="CacheOptions.MaxEntries"/> is exceeded.
    /// </summary>
    /// <remarks>
    /// This is the only mutable property on <see cref="CacheEntry"/>. It is
    /// updated atomically by the cache on each <c>GetAsync</c> call. For
    /// thread safety, reads and writes use <see cref="Interlocked"/> operations
    /// on the underlying ticks value.
    /// </remarks>
    internal DateTimeOffset LastAccessedAt { get; set; }

    /// <summary>
    /// Returns <c>true</c> if the entry's TTL has expired based on the current time.
    /// </summary>
    /// <param name="now">
    /// The current time to compare against <see cref="ExpiresAt"/>.
    /// If <c>null</c>, uses <see cref="DateTimeOffset.UtcNow"/>.
    /// </param>
    /// <returns><c>true</c> if the entry is expired; <c>false</c> otherwise.</returns>
    public bool IsExpired(DateTimeOffset? now = null)
    {
        var currentTime = now ?? DateTimeOffset.UtcNow;
        return currentTime >= ExpiresAt;
    }
}
