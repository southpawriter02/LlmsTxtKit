using LlmsTxtKit.Core.Caching;
using LlmsTxtKit.Core.Fetching;
using LlmsTxtKit.Core.Parsing;
using Xunit;

namespace LlmsTxtKit.Core.Tests;

/// <summary>
/// Unit tests for <see cref="LlmsTxtCache"/>, <see cref="CacheEntry"/>,
/// <see cref="FileCacheBackingStore"/>, and related caching infrastructure.
/// Tests cover TTL expiration, LRU eviction, stale-while-revalidate semantics,
/// backing store persistence, and concurrent access safety.
/// </summary>
/// <remarks>
/// All tests use in-memory constructs where possible. File-backed store tests
/// use a temporary directory that is cleaned up after each test.
/// </remarks>
public class CacheTests : IDisposable
{
    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    /// <summary>Well-formed llms.txt content for building test entries.</summary>
    private const string ValidContent = """
        # Test Site
        > A test site for cache tests.
        ## Docs
        - [Guide](https://example.com/guide.md): The guide
        """;

    /// <summary>Temp directory for file-backed store tests. Cleaned up in Dispose.</summary>
    private readonly string _tempDir;

    public CacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "llmstxtkit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// Creates a fresh <see cref="CacheEntry"/> with configurable TTL.
    /// </summary>
    /// <param name="ttl">Time-to-live from now. Default: 1 hour.</param>
    /// <param name="fetchedAt">When the entry was fetched. Default: now.</param>
    private static CacheEntry CreateEntry(TimeSpan? ttl = null, DateTimeOffset? fetchedAt = null)
    {
        var now = fetchedAt ?? DateTimeOffset.UtcNow;
        var effectiveTtl = ttl ?? TimeSpan.FromHours(1);
        var doc = LlmsDocumentParser.Parse(ValidContent);

        return new CacheEntry
        {
            Document = doc,
            FetchedAt = now,
            ExpiresAt = now + effectiveTtl,
            HttpHeaders = new Dictionary<string, string> { ["etag"] = "\"abc123\"" },
            FetchResult = new FetchResult
            {
                Status = FetchStatus.Success,
                Document = doc,
                RawContent = ValidContent,
                StatusCode = 200,
                Duration = TimeSpan.FromMilliseconds(150),
                Domain = "example.com"
            },
            LastAccessedAt = now
        };
    }

    /// <summary>
    /// Creates a <see cref="CacheEntry"/> that is already expired.
    /// </summary>
    private static CacheEntry CreateExpiredEntry()
    {
        // Fetched 2 hours ago with 1-hour TTL → expired 1 hour ago
        var fetchedAt = DateTimeOffset.UtcNow.AddHours(-2);
        return CreateEntry(ttl: TimeSpan.FromHours(1), fetchedAt: fetchedAt);
    }

    // ===============================================================
    // CacheEntry.IsExpired
    // ===============================================================

    /// <summary>
    /// A fresh entry (TTL not yet elapsed) reports <c>IsExpired == false</c>.
    /// </summary>
    [Fact]
    public void CacheEntry_IsExpired_FreshEntry_ReturnsFalse()
    {
        var entry = CreateEntry(ttl: TimeSpan.FromHours(1));
        Assert.False(entry.IsExpired());
    }

    /// <summary>
    /// An expired entry reports <c>IsExpired == true</c>.
    /// </summary>
    [Fact]
    public void CacheEntry_IsExpired_ExpiredEntry_ReturnsTrue()
    {
        var entry = CreateExpiredEntry();
        Assert.True(entry.IsExpired());
    }

    // ===============================================================
    // GetAsync — Cache Hit (Fresh)
    // ===============================================================

    /// <summary>
    /// Storing an entry and then retrieving it returns the same entry.
    /// </summary>
    [Fact]
    public async Task GetAsync_CachedEntry_ReturnsEntry()
    {
        var cache = new LlmsTxtCache();
        var entry = CreateEntry();

        await cache.SetAsync("example.com", entry);
        var result = await cache.GetAsync("example.com");

        Assert.NotNull(result);
        Assert.Equal("Test Site", result.Document.Title);
    }

    /// <summary>
    /// Cache lookups are case-insensitive on the domain name.
    /// </summary>
    [Fact]
    public async Task GetAsync_CaseInsensitiveLookup_ReturnsEntry()
    {
        var cache = new LlmsTxtCache();
        var entry = CreateEntry();

        await cache.SetAsync("Example.COM", entry);
        var result = await cache.GetAsync("example.com");

        Assert.NotNull(result);
    }

    // ===============================================================
    // GetAsync — Cache Miss
    // ===============================================================

    /// <summary>
    /// Getting an entry that was never cached returns <c>null</c>.
    /// </summary>
    [Fact]
    public async Task GetAsync_NoCachedEntry_ReturnsNull()
    {
        var cache = new LlmsTxtCache();
        var result = await cache.GetAsync("never-cached.com");

        Assert.Null(result);
    }

    // ===============================================================
    // GetAsync — Expired Entry + Stale-While-Revalidate
    // ===============================================================

    /// <summary>
    /// When stale-while-revalidate is enabled (default), expired entries
    /// are returned with <c>IsExpired == true</c>.
    /// </summary>
    [Fact]
    public async Task GetAsync_ExpiredEntry_StaleWhileRevalidateEnabled_ReturnsStaleEntry()
    {
        var options = new CacheOptions { StaleWhileRevalidate = true };
        var cache = new LlmsTxtCache(options);
        var entry = CreateExpiredEntry();

        await cache.SetAsync("example.com", entry);
        var result = await cache.GetAsync("example.com");

        Assert.NotNull(result);
        Assert.True(result.IsExpired());
    }

    /// <summary>
    /// When stale-while-revalidate is disabled, expired entries return <c>null</c>.
    /// </summary>
    [Fact]
    public async Task GetAsync_ExpiredEntry_StaleWhileRevalidateDisabled_ReturnsNull()
    {
        var options = new CacheOptions { StaleWhileRevalidate = false };
        var cache = new LlmsTxtCache(options);
        var entry = CreateExpiredEntry();

        await cache.SetAsync("example.com", entry);
        var result = await cache.GetAsync("example.com");

        Assert.Null(result);
    }

    // ===============================================================
    // SetAsync — LRU Eviction
    // ===============================================================

    /// <summary>
    /// When the cache exceeds <see cref="CacheOptions.MaxEntries"/>, the
    /// least-recently-used entry is evicted.
    /// </summary>
    [Fact]
    public async Task SetAsync_ExceedsMaxEntries_EvictsOldest()
    {
        var options = new CacheOptions { MaxEntries = 3 };
        var cache = new LlmsTxtCache(options);

        // Add 3 entries with staggered access times
        var entry1 = CreateEntry();
        entry1.LastAccessedAt = DateTimeOffset.UtcNow.AddMinutes(-30); // Oldest
        await cache.SetAsync("oldest.com", entry1);

        var entry2 = CreateEntry();
        entry2.LastAccessedAt = DateTimeOffset.UtcNow.AddMinutes(-15);
        await cache.SetAsync("middle.com", entry2);

        var entry3 = CreateEntry();
        entry3.LastAccessedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await cache.SetAsync("newest.com", entry3);

        Assert.Equal(3, cache.Count);

        // Add a 4th entry — should evict "oldest.com"
        await cache.SetAsync("fourth.com", CreateEntry());

        Assert.Equal(3, cache.Count);
        Assert.Null(await cache.GetAsync("oldest.com"));
        Assert.NotNull(await cache.GetAsync("middle.com"));
        Assert.NotNull(await cache.GetAsync("newest.com"));
        Assert.NotNull(await cache.GetAsync("fourth.com"));
    }

    // ===============================================================
    // InvalidateAsync
    // ===============================================================

    /// <summary>
    /// Invalidating a cached domain removes it from the cache.
    /// </summary>
    [Fact]
    public async Task InvalidateAsync_RemovesEntry()
    {
        var cache = new LlmsTxtCache();
        await cache.SetAsync("example.com", CreateEntry());

        Assert.Equal(1, cache.Count);

        await cache.InvalidateAsync("example.com");

        Assert.Equal(0, cache.Count);
        Assert.Null(await cache.GetAsync("example.com"));
    }

    /// <summary>
    /// Invalidating a domain that was never cached does not throw.
    /// </summary>
    [Fact]
    public async Task InvalidateAsync_NonExistentDomain_DoesNotThrow()
    {
        var cache = new LlmsTxtCache();
        await cache.InvalidateAsync("never-cached.com"); // Should not throw
    }

    // ===============================================================
    // ClearAsync
    // ===============================================================

    /// <summary>
    /// Clearing the cache removes all entries.
    /// </summary>
    [Fact]
    public async Task ClearAsync_RemovesAllEntries()
    {
        var cache = new LlmsTxtCache();
        await cache.SetAsync("a.com", CreateEntry());
        await cache.SetAsync("b.com", CreateEntry());
        await cache.SetAsync("c.com", CreateEntry());

        Assert.Equal(3, cache.Count);

        await cache.ClearAsync();

        Assert.Equal(0, cache.Count);
        Assert.Null(await cache.GetAsync("a.com"));
    }

    // ===============================================================
    // FileCacheBackingStore — Round-Trip
    // ===============================================================

    /// <summary>
    /// Saving an entry to the file-backed store and loading it back
    /// produces an equivalent entry (round-trip serialization).
    /// </summary>
    [Fact]
    public async Task FileCacheBackingStore_SaveAndLoad_RoundTrips()
    {
        var store = new FileCacheBackingStore(_tempDir);
        var original = CreateEntry();

        await store.SaveAsync("example.com", original);
        var loaded = await store.LoadAsync("example.com");

        Assert.NotNull(loaded);
        Assert.Equal(original.Document.Title, loaded.Document.Title);
        Assert.Equal(original.Document.Sections.Count, loaded.Document.Sections.Count);
        Assert.Equal(original.FetchedAt.UtcDateTime, loaded.FetchedAt.UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.Equal(original.ExpiresAt.UtcDateTime, loaded.ExpiresAt.UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.Equal(original.FetchResult.Status, loaded.FetchResult.Status);
        Assert.Equal(original.FetchResult.StatusCode, loaded.FetchResult.StatusCode);
        Assert.Equal(original.FetchResult.Domain, loaded.FetchResult.Domain);
    }

    /// <summary>
    /// Loading an entry that doesn't exist returns <c>null</c>.
    /// </summary>
    [Fact]
    public async Task FileCacheBackingStore_LoadNonExistent_ReturnsNull()
    {
        var store = new FileCacheBackingStore(_tempDir);
        var result = await store.LoadAsync("nonexistent.com");

        Assert.Null(result);
    }

    /// <summary>
    /// Removing an entry from the file-backed store makes it return <c>null</c>.
    /// </summary>
    [Fact]
    public async Task FileCacheBackingStore_Remove_DeletesFile()
    {
        var store = new FileCacheBackingStore(_tempDir);
        await store.SaveAsync("example.com", CreateEntry());

        Assert.NotNull(await store.LoadAsync("example.com"));

        await store.RemoveAsync("example.com");

        Assert.Null(await store.LoadAsync("example.com"));
    }

    /// <summary>
    /// Clearing the file-backed store removes all cached files.
    /// </summary>
    [Fact]
    public async Task FileCacheBackingStore_Clear_RemovesAllFiles()
    {
        var store = new FileCacheBackingStore(_tempDir);
        await store.SaveAsync("a.com", CreateEntry());
        await store.SaveAsync("b.com", CreateEntry());

        await store.ClearAsync();

        Assert.Null(await store.LoadAsync("a.com"));
        Assert.Null(await store.LoadAsync("b.com"));
    }

    // ===============================================================
    // Backing Store Integration
    // ===============================================================

    /// <summary>
    /// When a backing store is configured, in-memory misses fall back to
    /// the backing store and promote the entry to in-memory.
    /// </summary>
    [Fact]
    public async Task GetAsync_BackingStoreFallback_PromotesToMemory()
    {
        var store = new FileCacheBackingStore(_tempDir);
        var options = new CacheOptions { BackingStore = store };

        // Pre-populate the backing store directly
        await store.SaveAsync("example.com", CreateEntry());

        // Create a NEW cache (empty in-memory) that uses the store
        var cache = new LlmsTxtCache(options);

        Assert.Equal(0, cache.Count); // Nothing in memory yet

        var result = await cache.GetAsync("example.com");

        Assert.NotNull(result);
        Assert.Equal(1, cache.Count); // Promoted to in-memory
    }

    /// <summary>
    /// When a backing store is configured, <c>SetAsync</c> persists the entry.
    /// </summary>
    [Fact]
    public async Task SetAsync_BackingStoreConfigured_PersistsEntry()
    {
        var store = new FileCacheBackingStore(_tempDir);
        var options = new CacheOptions { BackingStore = store };
        var cache = new LlmsTxtCache(options);

        await cache.SetAsync("example.com", CreateEntry());

        // Verify the entry was persisted by loading directly from the store
        var persisted = await store.LoadAsync("example.com");
        Assert.NotNull(persisted);
    }

    /// <summary>
    /// When a backing store is configured, <c>InvalidateAsync</c> removes from both.
    /// </summary>
    [Fact]
    public async Task InvalidateAsync_BackingStoreConfigured_RemovesFromBoth()
    {
        var store = new FileCacheBackingStore(_tempDir);
        var options = new CacheOptions { BackingStore = store };
        var cache = new LlmsTxtCache(options);

        await cache.SetAsync("example.com", CreateEntry());
        await cache.InvalidateAsync("example.com");

        Assert.Equal(0, cache.Count);
        Assert.Null(await store.LoadAsync("example.com"));
    }

    // ===============================================================
    // Concurrent Access
    // ===============================================================

    /// <summary>
    /// Multiple threads reading and writing simultaneously don't corrupt
    /// the cache state.
    /// </summary>
    [Fact]
    public async Task ConcurrentAccess_ThreadSafe()
    {
        var cache = new LlmsTxtCache(new CacheOptions { MaxEntries = 50 });
        var tasks = new List<Task>();

        // Spawn 20 writers and 20 readers concurrently
        for (int i = 0; i < 20; i++)
        {
            var domain = $"domain{i}.com";
            tasks.Add(Task.Run(async () =>
            {
                await cache.SetAsync(domain, CreateEntry());
            }));
            tasks.Add(Task.Run(async () =>
            {
                // May return null (race) or the entry — either is fine
                await cache.GetAsync(domain);
            }));
        }

        // Should complete without exceptions
        await Task.WhenAll(tasks);

        // Cache should have some entries (exact count depends on race conditions)
        Assert.True(cache.Count >= 0 && cache.Count <= 50);
    }

    // ===============================================================
    // Null Input Validation
    // ===============================================================

    /// <summary>
    /// Passing <c>null</c> domain to <c>GetAsync</c> throws <see cref="ArgumentNullException"/>.
    /// </summary>
    [Fact]
    public async Task GetAsync_NullDomain_ThrowsArgumentNullException()
    {
        var cache = new LlmsTxtCache();
        await Assert.ThrowsAsync<ArgumentNullException>(() => cache.GetAsync(null!));
    }

    /// <summary>
    /// Passing <c>null</c> domain to <c>SetAsync</c> throws <see cref="ArgumentNullException"/>.
    /// </summary>
    [Fact]
    public async Task SetAsync_NullDomain_ThrowsArgumentNullException()
    {
        var cache = new LlmsTxtCache();
        await Assert.ThrowsAsync<ArgumentNullException>(() => cache.SetAsync(null!, CreateEntry()));
    }

    /// <summary>
    /// Passing <c>null</c> entry to <c>SetAsync</c> throws <see cref="ArgumentNullException"/>.
    /// </summary>
    [Fact]
    public async Task SetAsync_NullEntry_ThrowsArgumentNullException()
    {
        var cache = new LlmsTxtCache();
        await Assert.ThrowsAsync<ArgumentNullException>(() => cache.SetAsync("example.com", null!));
    }
}
