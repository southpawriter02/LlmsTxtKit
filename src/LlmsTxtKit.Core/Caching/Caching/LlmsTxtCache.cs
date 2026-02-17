using System.Collections.Concurrent;

namespace LlmsTxtKit.Core.Caching;

/// <summary>
/// Domain-keyed cache for parsed llms.txt documents. Provides in-memory storage
/// with configurable TTL, LRU eviction, stale-while-revalidate semantics, and
/// optional persistent backing store.
/// </summary>
/// <remarks>
/// <para>
/// Cache entries are keyed by domain name (e.g., "anthropic.com"), not by full URL.
/// This works because each domain has exactly one <c>/llms.txt</c> endpoint per the
/// llms.txt specification. The simplified keying makes the API intuitive:
/// <c>GetAsync("anthropic.com")</c> rather than
/// <c>GetAsync("https://docs.anthropic.com/llms.txt")</c>.
/// </para>
/// <para>
/// <b>Thread Safety:</b> All in-memory operations use a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> for lock-free concurrent access.
/// The LRU eviction algorithm uses a lock to prevent multiple eviction runs from
/// interfering with each other, but reads and writes are not blocked during eviction.
/// </para>
/// <para>
/// <b>Stale-While-Revalidate:</b> When <see cref="CacheOptions.StaleWhileRevalidate"/>
/// is enabled, expired entries are returned from <c>GetAsync</c> with
/// <see cref="CacheEntry.IsExpired"/> returning <c>true</c>. The cache does NOT
/// automatically trigger background refreshes — the caller is responsible for
/// detecting staleness and re-fetching as needed. This design keeps the cache
/// simple and predictable (no hidden background tasks or timers).
/// </para>
/// <para>
/// <b>Backing Store Integration:</b> When a <see cref="ICacheBackingStore"/> is
/// configured via <see cref="CacheOptions.BackingStore"/>, the cache operates as
/// a write-through cache: entries are persisted on <c>SetAsync</c> and loaded
/// from the backing store on in-memory misses. This provides persistence across
/// process restarts without changing the caller's API.
/// </para>
/// </remarks>
public sealed class LlmsTxtCache
{
    /// <summary>
    /// The in-memory cache store. Keys are lowercase domain names.
    /// </summary>
    private readonly ConcurrentDictionary<string, CacheEntry> _store = new();

    /// <summary>
    /// Configuration options controlling TTL, max entries, eviction, and backing store.
    /// </summary>
    private readonly CacheOptions _options;

    /// <summary>
    /// Lock object for the eviction algorithm. Only one eviction can run at a time,
    /// but reads and writes are not blocked during eviction.
    /// </summary>
    private readonly object _evictionLock = new();

    /// <summary>
    /// Creates a new cache with the specified options. If no options are provided,
    /// default values are used (1-hour TTL, 1000 max entries, stale-while-revalidate
    /// enabled, no backing store).
    /// </summary>
    /// <param name="options">
    /// Configuration options. If <c>null</c>, defaults are used.
    /// </param>
    public LlmsTxtCache(CacheOptions? options = null)
    {
        _options = options ?? new CacheOptions();
    }

    /// <summary>
    /// The number of entries currently in the in-memory cache.
    /// </summary>
    /// <remarks>
    /// This reflects only the in-memory store, not entries that may exist
    /// in the backing store but haven't been loaded yet.
    /// </remarks>
    public int Count => _store.Count;

    /// <summary>
    /// Retrieves a cached entry for the specified domain, or <c>null</c> if
    /// no entry exists or the entry has expired (and stale-while-revalidate
    /// is disabled).
    /// </summary>
    /// <param name="domain">
    /// The domain name to look up (e.g., "anthropic.com"). Case-insensitive.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The cached <see cref="CacheEntry"/> if found and fresh (or stale when
    /// stale-while-revalidate is enabled); <c>null</c> otherwise.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="domain"/> is <c>null</c>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Lookup order:
    /// 1. Check in-memory store.
    /// 2. On miss, check backing store (if configured) and promote to in-memory.
    /// 3. Apply TTL and stale-while-revalidate logic.
    /// </para>
    /// <para>
    /// Each successful lookup updates the entry's <see cref="CacheEntry.LastAccessedAt"/>
    /// timestamp for LRU eviction tracking.
    /// </para>
    /// </remarks>
    public async Task<CacheEntry?> GetAsync(string domain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domain);

        var key = NormalizeKey(domain);

        // 1. Check in-memory store
        if (_store.TryGetValue(key, out var entry))
        {
            return ProcessEntry(entry);
        }

        // 2. Fallback to backing store
        if (_options.BackingStore != null)
        {
            var loaded = await _options.BackingStore.LoadAsync(key, cancellationToken).ConfigureAwait(false);
            if (loaded != null)
            {
                // Promote to in-memory cache
                _store.TryAdd(key, loaded);
                return ProcessEntry(loaded);
            }
        }

        return null;
    }

    /// <summary>
    /// Stores or updates a cache entry for the specified domain. If the cache
    /// exceeds <see cref="CacheOptions.MaxEntries"/>, the least-recently-used
    /// entry is evicted.
    /// </summary>
    /// <param name="domain">
    /// The domain name to cache under (e.g., "anthropic.com"). Case-insensitive.
    /// </param>
    /// <param name="entry">The cache entry to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="domain"/> or <paramref name="entry"/> is <c>null</c>.
    /// </exception>
    /// <remarks>
    /// If a backing store is configured, the entry is also persisted to it
    /// (write-through pattern). LRU eviction occurs after the entry is stored,
    /// so the new entry is never the one evicted.
    /// </remarks>
    public async Task SetAsync(string domain, CacheEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(entry);

        var key = NormalizeKey(domain);

        // Update the access timestamp
        entry.LastAccessedAt = DateTimeOffset.UtcNow;

        // Store in memory (add or replace)
        _store[key] = entry;

        // Persist to backing store if configured
        if (_options.BackingStore != null)
        {
            await _options.BackingStore.SaveAsync(key, entry, cancellationToken).ConfigureAwait(false);
        }

        // Evict if over capacity
        EvictIfNeeded();
    }

    /// <summary>
    /// Removes the cached entry for the specified domain from both the in-memory
    /// store and the backing store (if configured).
    /// </summary>
    /// <param name="domain">
    /// The domain name to invalidate (e.g., "anthropic.com"). Case-insensitive.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="domain"/> is <c>null</c>.
    /// </exception>
    /// <remarks>
    /// This is an idempotent operation — calling it for a domain that isn't
    /// cached has no effect and does not throw.
    /// </remarks>
    public async Task InvalidateAsync(string domain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domain);

        var key = NormalizeKey(domain);
        _store.TryRemove(key, out _);

        if (_options.BackingStore != null)
        {
            await _options.BackingStore.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes all entries from both the in-memory store and the backing store
    /// (if configured).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _store.Clear();

        if (_options.BackingStore != null)
        {
            await _options.BackingStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------
    // Private Helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Normalizes a domain key to lowercase for case-insensitive lookups.
    /// </summary>
    private static string NormalizeKey(string domain) => domain.Trim().ToLowerInvariant();

    /// <summary>
    /// Applies TTL and stale-while-revalidate logic to a retrieved entry.
    /// Updates the access timestamp on hit.
    /// </summary>
    /// <param name="entry">The entry from the cache store.</param>
    /// <returns>
    /// The entry if it is fresh or if stale-while-revalidate is enabled;
    /// <c>null</c> if the entry is expired and stale-while-revalidate is disabled.
    /// </returns>
    private CacheEntry? ProcessEntry(CacheEntry entry)
    {
        // Update LRU timestamp
        entry.LastAccessedAt = DateTimeOffset.UtcNow;

        // If fresh, return as-is
        if (!entry.IsExpired())
            return entry;

        // If stale-while-revalidate is enabled, return stale entry
        if (_options.StaleWhileRevalidate)
            return entry;

        // Entry is expired and stale-while-revalidate is off — treat as miss
        return null;
    }

    /// <summary>
    /// Evicts the least-recently-used entries if the cache exceeds
    /// <see cref="CacheOptions.MaxEntries"/>. Uses a simple scan-and-remove
    /// approach under a lock to prevent concurrent eviction runs.
    /// </summary>
    /// <remarks>
    /// The eviction algorithm is O(n) where n is the number of entries. For the
    /// default <c>MaxEntries</c> of 1000, this is negligible. If much larger caches
    /// are needed, consider replacing with a linked-list-based LRU structure.
    /// </remarks>
    private void EvictIfNeeded()
    {
        if (_store.Count <= _options.MaxEntries)
            return;

        lock (_evictionLock)
        {
            // Re-check under lock (another thread may have already evicted)
            while (_store.Count > _options.MaxEntries)
            {
                // Find the entry with the oldest LastAccessedAt
                string? oldestKey = null;
                var oldestAccess = DateTimeOffset.MaxValue;

                foreach (var kvp in _store)
                {
                    if (kvp.Value.LastAccessedAt < oldestAccess)
                    {
                        oldestAccess = kvp.Value.LastAccessedAt;
                        oldestKey = kvp.Key;
                    }
                }

                if (oldestKey != null)
                {
                    _store.TryRemove(oldestKey, out _);
                }
                else
                {
                    break; // Safety valve — shouldn't happen
                }
            }
        }
    }
}
