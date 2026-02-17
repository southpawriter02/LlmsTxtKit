namespace LlmsTxtKit.Core.Caching;

/// <summary>
/// Abstraction for persistent storage of cache entries. Implementations provide
/// a simple key-value store interface for saving and loading <see cref="CacheEntry"/>
/// instances beyond the lifetime of the in-memory cache.
/// </summary>
/// <remarks>
/// <para>
/// The default <see cref="LlmsTxtCache"/> uses an in-memory
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// as its primary store. When a backing store is configured via
/// <see cref="CacheOptions.BackingStore"/>, the cache will persist entries to it
/// and fall back to it on in-memory misses.
/// </para>
/// <para>
/// The built-in implementation is <see cref="FileCacheBackingStore"/>, which
/// serializes entries to JSON files. Custom implementations can target Redis,
/// SQLite, or any other storage backend without changing the Core library.
/// </para>
/// <para>
/// All methods are async to support I/O-bound backing stores (file system,
/// network storage, databases). In-memory-only implementations can return
/// completed tasks.
/// </para>
/// </remarks>
public interface ICacheBackingStore
{
    /// <summary>
    /// Persists a cache entry under the specified key. If an entry with the
    /// same key already exists, it is overwritten.
    /// </summary>
    /// <param name="key">
    /// The cache key (domain name, e.g., "anthropic.com").
    /// </param>
    /// <param name="entry">The cache entry to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the entry has been persisted.</returns>
    Task SaveAsync(string key, CacheEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a cache entry by key, or returns <c>null</c> if no entry exists
    /// for the specified key in the backing store.
    /// </summary>
    /// <param name="key">
    /// The cache key (domain name, e.g., "anthropic.com").
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The cached entry, or <c>null</c> if no entry was found.
    /// </returns>
    Task<CacheEntry?> LoadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a specific entry from the backing store. If no entry exists
    /// for the key, this method completes successfully (idempotent delete).
    /// </summary>
    /// <param name="key">
    /// The cache key (domain name) to remove.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the entry has been removed.</returns>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all entries from the backing store. After this call,
    /// <see cref="LoadAsync"/> should return <c>null</c> for all keys.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the store has been cleared.</returns>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
