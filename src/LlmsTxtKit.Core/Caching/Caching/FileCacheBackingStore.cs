using System.Text.Json;
using System.Text.Json.Serialization;
using LlmsTxtKit.Core.Fetching;
using LlmsTxtKit.Core.Parsing;
using LlmsTxtKit.Core.Validation;

namespace LlmsTxtKit.Core.Caching;

/// <summary>
/// A file-backed implementation of <see cref="ICacheBackingStore"/> that serializes
/// cache entries to individual JSON files in a configurable directory. Each domain
/// gets its own file, named by a sanitized version of the domain name.
/// </summary>
/// <remarks>
/// <para>
/// File layout: <c>{CacheDirectory}/{sanitized-domain}.json</c>. For example,
/// caching "anthropic.com" with the default directory would produce
/// <c>~/.llmstxtkit/cache/anthropic_com.json</c>.
/// </para>
/// <para>
/// Because <see cref="CacheEntry"/> contains complex types like
/// <see cref="LlmsDocument"/> (immutable records with <c>init</c>-only properties),
/// this implementation serializes a simplified DTO (<see cref="CacheEntryDto"/>)
/// that captures the essential data. On load, the DTO is re-hydrated into a full
/// <see cref="CacheEntry"/> by re-parsing the raw content through
/// <see cref="LlmsDocumentParser.Parse"/>.
/// </para>
/// <para>
/// Thread safety: Individual file operations use atomic write patterns (write to
/// temp file, then rename) to prevent corruption from concurrent access. However,
/// this implementation does not provide cross-process locking — if multiple
/// processes share the same cache directory, last-write-wins semantics apply.
/// </para>
/// </remarks>
public sealed class FileCacheBackingStore : ICacheBackingStore
{
    /// <summary>
    /// The directory where cache files are stored.
    /// </summary>
    private readonly string _cacheDirectory;

    /// <summary>
    /// JSON serialization options used for all file I/O.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Creates a new file-backed cache store with the specified directory.
    /// The directory is created if it does not exist.
    /// </summary>
    /// <param name="cacheDirectory">
    /// The directory path for cache files. If <c>null</c> or empty, defaults
    /// to <c>~/.llmstxtkit/cache</c> (using the user's home directory).
    /// </param>
    public FileCacheBackingStore(string? cacheDirectory = null)
    {
        _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".llmstxtkit", "cache")
            : cacheDirectory;

        Directory.CreateDirectory(_cacheDirectory);
    }

    /// <inheritdoc/>
    public async Task SaveAsync(string key, CacheEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(entry);

        var dto = CacheEntryDto.FromCacheEntry(key, entry);
        var filePath = GetFilePath(key);
        var tempPath = filePath + ".tmp";

        // Atomic write: serialize to temp file, then rename to avoid corruption
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, filePath, overwrite: true);
    }

    /// <inheritdoc/>
    public async Task<CacheEntry?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var filePath = GetFilePath(key);
        if (!File.Exists(filePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<CacheEntryDto>(json, JsonOptions);
            return dto?.ToCacheEntry();
        }
        catch (JsonException)
        {
            // Corrupted file — treat as a miss. The next SetAsync will overwrite it.
            return null;
        }
        catch (IOException)
        {
            // File was deleted or is being written by another process — treat as a miss.
            return null;
        }
    }

    /// <inheritdoc/>
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var filePath = GetFilePath(key);
        if (File.Exists(filePath))
            File.Delete(filePath);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(_cacheDirectory))
        {
            foreach (var file in Directory.GetFiles(_cacheDirectory, "*.json"))
            {
                File.Delete(file);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Computes the file path for a given domain key by sanitizing the domain
    /// name (replacing dots and other unsafe characters with underscores).
    /// </summary>
    /// <param name="key">The domain name key.</param>
    /// <returns>The full file path for the cache entry.</returns>
    private string GetFilePath(string key)
    {
        // Sanitize the domain name for use as a filename
        var sanitized = key
            .Replace('.', '_')
            .Replace(':', '_')
            .Replace('/', '_')
            .Replace('\\', '_');
        return Path.Combine(_cacheDirectory, sanitized + ".json");
    }

    // ---------------------------------------------------------------
    // Serialization DTO
    // ---------------------------------------------------------------

    /// <summary>
    /// A simplified data transfer object for serializing <see cref="CacheEntry"/>
    /// to JSON. The full <see cref="LlmsDocument"/> is not serialized directly
    /// (it contains complex immutable records); instead, we store the raw content
    /// and re-parse on load.
    /// </summary>
    internal sealed class CacheEntryDto
    {
        /// <summary>The domain this entry is for.</summary>
        public string Domain { get; set; } = string.Empty;

        /// <summary>The raw llms.txt content (re-parsed on load).</summary>
        public string RawContent { get; set; } = string.Empty;

        /// <summary>When the content was fetched.</summary>
        public DateTimeOffset FetchedAt { get; set; }

        /// <summary>When the TTL expires.</summary>
        public DateTimeOffset ExpiresAt { get; set; }

        /// <summary>When this entry was last accessed.</summary>
        public DateTimeOffset LastAccessedAt { get; set; }

        /// <summary>HTTP headers from the fetch response.</summary>
        public Dictionary<string, string>? HttpHeaders { get; set; }

        /// <summary>The fetch status that produced this entry.</summary>
        public FetchStatus FetchStatus { get; set; }

        /// <summary>The HTTP status code from the fetch.</summary>
        public int? StatusCode { get; set; }

        /// <summary>The fetch duration in milliseconds.</summary>
        public double DurationMs { get; set; }

        /// <summary>
        /// Creates a DTO from a <see cref="CacheEntry"/> for serialization.
        /// </summary>
        public static CacheEntryDto FromCacheEntry(string domain, CacheEntry entry) => new()
        {
            Domain = domain,
            RawContent = entry.Document.RawContent,
            FetchedAt = entry.FetchedAt,
            ExpiresAt = entry.ExpiresAt,
            LastAccessedAt = entry.LastAccessedAt,
            HttpHeaders = entry.HttpHeaders != null
                ? new Dictionary<string, string>(entry.HttpHeaders)
                : null,
            FetchStatus = entry.FetchResult.Status,
            StatusCode = entry.FetchResult.StatusCode,
            DurationMs = entry.FetchResult.Duration.TotalMilliseconds
        };

        /// <summary>
        /// Re-hydrates a <see cref="CacheEntry"/> from this DTO by re-parsing
        /// the raw content through <see cref="LlmsDocumentParser.Parse"/>.
        /// </summary>
        public CacheEntry ToCacheEntry()
        {
            var document = LlmsDocumentParser.Parse(RawContent);
            var headers = HttpHeaders != null
                ? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(HttpHeaders)
                : null;

            var fetchResult = new FetchResult
            {
                Status = FetchStatus,
                Document = FetchStatus == FetchStatus.Success ? document : null,
                RawContent = RawContent,
                StatusCode = StatusCode,
                ResponseHeaders = headers,
                Duration = TimeSpan.FromMilliseconds(DurationMs),
                Domain = Domain
            };

            return new CacheEntry
            {
                Document = document,
                FetchedAt = FetchedAt,
                ExpiresAt = ExpiresAt,
                HttpHeaders = headers,
                FetchResult = fetchResult,
                LastAccessedAt = LastAccessedAt
            };
        }
    }
}
