namespace LlmsTxtKit.Core.Caching;

/// <summary>
/// Configuration options for <see cref="LlmsTxtCache"/>. Controls TTL duration,
/// maximum cache size, stale-while-revalidate behavior, and optional persistent
/// backing store.
/// </summary>
/// <remarks>
/// <para>
/// Default values are designed for a typical MCP server or CLI tool use case:
/// a 1-hour TTL balances freshness with fetch frequency, 1000 max entries is
/// generous for most workloads, and stale-while-revalidate is enabled so that
/// AI inference pipelines get immediate responses even when entries are expired.
/// </para>
/// <para>
/// For high-throughput scenarios, consider increasing <see cref="MaxEntries"/>
/// and providing a <see cref="BackingStore"/> for persistence across process restarts.
/// For real-time scenarios, decrease <see cref="DefaultTtl"/> or disable
/// <see cref="StaleWhileRevalidate"/>.
/// </para>
/// </remarks>
public sealed class CacheOptions
{
    /// <summary>
    /// How long cache entries live before they are considered stale.
    /// Default: 1 hour.
    /// </summary>
    /// <remarks>
    /// When an entry's TTL expires, the behavior depends on
    /// <see cref="StaleWhileRevalidate"/>:
    /// <list type="bullet">
    /// <item>If enabled, the stale entry is returned immediately while a
    ///   background refresh is triggered.</item>
    /// <item>If disabled, the expired entry is treated as a miss and
    ///   <c>GetAsync</c> returns <c>null</c>.</item>
    /// </list>
    /// </remarks>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum number of cached domains. When this limit is exceeded, the
    /// least-recently-used (LRU) entry is evicted to make room.
    /// Default: 1000.
    /// </summary>
    /// <remarks>
    /// Each entry is keyed by domain name (e.g., "anthropic.com"), not by
    /// full URL. Since each domain maps to exactly one <c>/llms.txt</c> path,
    /// this effectively limits the number of distinct domains cached.
    /// </remarks>
    public int MaxEntries { get; set; } = 1000;

    /// <summary>
    /// When <c>true</c>, expired cache entries are returned immediately
    /// (marked as stale) while a background refresh can be triggered by
    /// the caller. When <c>false</c>, expired entries are treated as misses.
    /// Default: <c>true</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stale-while-revalidate pattern is particularly valuable for AI
    /// inference use cases where latency matters more than absolute freshness.
    /// Returning stale content immediately avoids blocking on a re-fetch that
    /// might fail due to network issues, rate limiting, or WAF blocks.
    /// </para>
    /// <para>
    /// When this option is enabled, the cache itself does NOT automatically
    /// trigger a background refresh. It simply returns the stale entry with
    /// <see cref="CacheEntry.IsExpired"/> returning <c>true</c>. The caller
    /// is responsible for deciding whether to re-fetch and update the cache.
    /// This keeps the cache simple and avoids hidden background work.
    /// </para>
    /// </remarks>
    public bool StaleWhileRevalidate { get; set; } = true;

    /// <summary>
    /// Optional backing store for persistent caching. When <c>null</c>
    /// (the default), entries live only in memory and are lost when the
    /// process exits. Set to a <see cref="FileCacheBackingStore"/> or
    /// custom <see cref="ICacheBackingStore"/> implementation for persistence.
    /// </summary>
    /// <remarks>
    /// When a backing store is configured, <see cref="LlmsTxtCache"/> will:
    /// <list type="bullet">
    /// <item>Persist new/updated entries via <see cref="ICacheBackingStore.SaveAsync"/>
    ///   on each <c>SetAsync</c> call.</item>
    /// <item>Attempt to load entries from the backing store on <c>GetAsync</c>
    ///   cache misses (fallback from in-memory to persistent store).</item>
    /// <item>Remove entries from the backing store on <c>InvalidateAsync</c>
    ///   and <c>ClearAsync</c>.</item>
    /// </list>
    /// </remarks>
    public ICacheBackingStore? BackingStore { get; set; }
}
