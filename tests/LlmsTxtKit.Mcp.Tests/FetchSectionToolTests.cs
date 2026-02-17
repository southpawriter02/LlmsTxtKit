using System.Text.Json;
using LlmsTxtKit.Core.Caching;
using LlmsTxtKit.Core.Context;
using LlmsTxtKit.Core.Fetching;
using LlmsTxtKit.Core.Parsing;
using LlmsTxtKit.Mcp.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlmsTxtKit.Mcp.Tests;

/// <summary>
/// Unit tests for the <c>llmstxt_fetch_section</c> MCP tool (<see cref="FetchSectionTool"/>).
/// Tests verify the tool's ability to locate named sections, fetch linked content,
/// handle missing sections, clean fetched content, and deal with fetch errors.
/// </summary>
/// <remarks>
/// <para>
/// These tests exercise <see cref="FetchSectionTool.FetchSection"/> directly,
/// injecting mocks for HTTP (via <see cref="MockHttpHandler"/>) and content
/// fetching (via <see cref="MockContentFetcher"/>). This tests the tool logic
/// end-to-end — from input validation through JSON serialization — without
/// requiring a running MCP server or network access.
/// </para>
/// </remarks>
public class FetchSectionToolTests : IDisposable
{
    // ---------------------------------------------------------------
    // Test Infrastructure
    // ---------------------------------------------------------------

    /// <summary>
    /// Well-formed llms.txt content with two sections: Docs (2 entries) and
    /// Optional (1 entry). Used to test section lookup and content fetching.
    /// </summary>
    private const string ValidLlmsTxt = """
        # TestSite
        > A test site for llms.txt testing.
        ## Docs
        - [API Reference](https://example.com/api.md): Complete API docs
        - [Getting Started](https://example.com/guide.md): Quickstart guide
        ## Optional
        - [Advanced Topics](https://example.com/advanced.md): Deep dive topics
        """;

    /// <summary>Logger instance (discards output — tests don't need log assertions).</summary>
    private readonly ILogger<FetchSectionTool> _logger = NullLogger<FetchSectionTool>.Instance;

    /// <summary>Cache instance (fresh per test).</summary>
    private readonly LlmsTxtCache _cache = new(new CacheOptions { StaleWhileRevalidate = false });

    /// <summary>Track disposable fetchers for cleanup.</summary>
    private readonly List<LlmsTxtFetcher> _fetchers = new();

    /// <summary>JSON parse options for deserializing tool responses.</summary>
    private static readonly JsonDocumentOptions JsonParseOptions = new() { AllowTrailingCommas = true };

    public void Dispose()
    {
        foreach (var fetcher in _fetchers)
            fetcher.Dispose();
    }

    /// <summary>
    /// Creates an <see cref="LlmsTxtFetcher"/> backed by a mock HTTP handler.
    /// </summary>
    private LlmsTxtFetcher CreateMockFetcher(int statusCode, string content)
    {
        var handler = new MockHttpHandler(_ =>
            new HttpResponseMessage((System.Net.HttpStatusCode)statusCode)
            {
                Content = new StringContent(content)
            });
        var httpClient = new HttpClient(handler);
        var fetcher = new LlmsTxtFetcher(new FetcherOptions { MaxRetries = 0 }, httpClient);
        _fetchers.Add(fetcher);
        return fetcher;
    }

    /// <summary>
    /// Creates a fetcher that simulates DNS failure.
    /// </summary>
    private LlmsTxtFetcher CreateDnsFailureFetcher()
    {
        var handler = new MockHttpHandler(_ =>
            throw new HttpRequestException("No such host is known."));
        var httpClient = new HttpClient(handler);
        var fetcher = new LlmsTxtFetcher(new FetcherOptions { MaxRetries = 0 }, httpClient);
        _fetchers.Add(fetcher);
        return fetcher;
    }

    /// <summary>Parses a JSON tool response for assertion.</summary>
    private static JsonDocument ParseResponse(string json) =>
        JsonDocument.Parse(json, JsonParseOptions);

    /// <summary>
    /// Pre-populates the cache with a parsed llms.txt document, allowing tests
    /// to bypass the HTTP fetch layer and test section logic directly.
    /// </summary>
    private async Task SeedCacheAsync(string domain, string content)
    {
        var doc = LlmsDocumentParser.Parse(content);
        var entry = new CacheEntry
        {
            Document = doc,
            FetchedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            FetchResult = new FetchResult
            {
                Status = FetchStatus.Success,
                Document = doc,
                RawContent = content,
                StatusCode = 200,
                Duration = TimeSpan.FromMilliseconds(50),
                Domain = domain
            }
        };
        await _cache.SetAsync(domain, entry);
    }

    // ===============================================================
    // Success Scenarios
    // ===============================================================

    /// <summary>
    /// Fetching a valid section returns success with entry content populated
    /// from the mock content fetcher.
    /// </summary>
    [Fact]
    public async Task FetchSectionTool_ValidSection_ReturnsContent()
    {
        // Arrange: seed cache with parsed llms.txt
        await SeedCacheAsync("test.com", ValidLlmsTxt);

        // Mock content fetcher returns Markdown for each URL
        var contentFetcher = new MockContentFetcher(new Dictionary<string, string>
        {
            ["https://example.com/api.md"] = "# API Reference\nThis is the API documentation.",
            ["https://example.com/guide.md"] = "# Getting Started\nFollow these steps to get started."
        });

        // Act
        var result = await FetchSectionTool.FetchSection(
            "test.com", "Docs",
            CreateDnsFailureFetcher(), // Won't be called (cache hit)
            _cache, contentFetcher, _logger);

        // Assert
        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("test.com", root.GetProperty("domain").GetString());
        Assert.Equal("Docs", root.GetProperty("section").GetString());
        Assert.Equal(2, root.GetProperty("entryCount").GetInt32());

        // Verify entry content
        var entries = root.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());

        var firstEntry = entries[0];
        Assert.Equal("API Reference", firstEntry.GetProperty("title").GetString());
        Assert.Contains("API documentation", firstEntry.GetProperty("content").GetString());
        // Error should not be present on success (WhenWritingNull omits it entirely)
        Assert.False(firstEntry.TryGetProperty("error", out _),
            "Error field should not be present on a successful entry");
    }

    /// <summary>
    /// Section name matching is case-insensitive: requesting "docs" matches "Docs".
    /// </summary>
    [Fact]
    public async Task FetchSectionTool_CaseInsensitiveSectionName_Matches()
    {
        await SeedCacheAsync("test.com", ValidLlmsTxt);
        var contentFetcher = new MockContentFetcher(new Dictionary<string, string>
        {
            ["https://example.com/api.md"] = "# API",
            ["https://example.com/guide.md"] = "# Guide"
        });

        // Use lowercase "docs" to match "Docs"
        var result = await FetchSectionTool.FetchSection(
            "test.com", "docs",
            CreateDnsFailureFetcher(), _cache, contentFetcher, _logger);

        using var doc = ParseResponse(result);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Docs", doc.RootElement.GetProperty("section").GetString());
    }

    /// <summary>
    /// When the llms.txt document is not cached, the tool fetches it from origin
    /// and then fetches section content. Verifies the full fetch → parse → section
    /// → content pipeline.
    /// </summary>
    [Fact]
    public async Task FetchSectionTool_NoCacheHit_FetchesFromOrigin()
    {
        var fetcher = CreateMockFetcher(200, ValidLlmsTxt);
        var contentFetcher = new MockContentFetcher(new Dictionary<string, string>
        {
            ["https://example.com/api.md"] = "# API",
            ["https://example.com/guide.md"] = "# Guide"
        });

        var result = await FetchSectionTool.FetchSection(
            "fresh.com", "Docs", fetcher, _cache, contentFetcher, _logger);

        using var doc = ParseResponse(result);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("fromCache").GetBoolean());
    }

    /// <summary>
    /// When fetched from cache, fromCache flag is true.
    /// </summary>
    [Fact]
    public async Task FetchSectionTool_CacheHit_SetsFromCacheTrue()
    {
        await SeedCacheAsync("cached.com", ValidLlmsTxt);
        var contentFetcher = new MockContentFetcher(new Dictionary<string, string>
        {
            ["https://example.com/api.md"] = "# API",
            ["https://example.com/guide.md"] = "# Guide"
        });

        var result = await FetchSectionTool.FetchSection(
            "cached.com", "Docs",
            CreateDnsFailureFetcher(), _cache, contentFetcher, _logger);

        using var doc = ParseResponse(result);
        Assert.True(doc.RootElement.GetProperty("fromCache").GetBoolean());
    }

    // ===============================================================
    // Section Not Found
    // ===============================================================

    /// <summary>
    /// Requesting a section that doesn't exist returns an error with the list
    /// of available sections, enabling the agent to self-correct.
    /// </summary>
    [Fact]
    public async Task FetchSectionTool_NonexistentSection_ReturnsErrorWithAvailableSections()
    {
        await SeedCacheAsync("test.com", ValidLlmsTxt);
        var contentFetcher = new MockContentFetcher(new Dictionary<string, string>());

        var result = await FetchSectionTool.FetchSection(
            "test.com", "NonexistentSection",
            CreateDnsFailureFetcher(), _cache, contentFetcher, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Contains("not found", root.GetProperty("errorMessage").GetString()!,
            StringComparison.OrdinalIgnoreCase);

        // Should include available sections
        var available = root.GetProperty("availableSections");
        Assert.Equal(JsonValueKind.Array, available.ValueKind);
        Assert.True(available.GetArrayLength() >= 2);

        var sectionNames = Enumerable.Range(0, available.GetArrayLength())
            .Select(i => available[i].GetString())
            .ToList();
        Assert.Contains("Docs", sectionNames);
        Assert.Contains("Optional", sectionNames);
    }

    // ===============================================================
    // Content Fetch Errors
    // ===============================================================

    /// <summary>
    /// When individual entry content fails to fetch, the entry has an error
    /// field (not content) and the response includes a fetchErrors count.
    /// </summary>
    [Fact]
    public async Task FetchSectionTool_EntryFetchFailure_ReturnsErrorInEntry()
    {
        await SeedCacheAsync("test.com", ValidLlmsTxt);

        // Only one URL succeeds; the other fails
        var contentFetcher = new MockContentFetcher(
            successes: new Dictionary<string, string>
            {
                ["https://example.com/api.md"] = "# API Reference"
            },
            failures: new Dictionary<string, string>
            {
                ["https://example.com/guide.md"] = "HTTP 500 Internal Server Error"
            });

        var result = await FetchSectionTool.FetchSection(
            "test.com", "Docs",
            CreateDnsFailureFetcher(), _cache, contentFetcher, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(1, root.GetProperty("fetchErrors").GetInt32());

        // Verify the failed entry has an error field (and no content)
        var entries = root.GetProperty("entries");
        var failedEntry = entries[1]; // guide.md
        Assert.True(failedEntry.TryGetProperty("error", out var errorProp),
            "Failed entry should have 'error' field");
        Assert.NotNull(errorProp.GetString());
        // Content should be absent (WhenWritingNull omits null fields)
        Assert.False(failedEntry.TryGetProperty("content", out _),
            "Failed entry should not have 'content' field");
    }

    /// <summary>
    /// When all entries fail to fetch, fetchErrors equals entryCount.
    /// </summary>
    [Fact]
    public async Task FetchSectionTool_AllEntriesFail_ReturnsAllErrors()
    {
        await SeedCacheAsync("test.com", ValidLlmsTxt);

        var contentFetcher = new MockContentFetcher(
            successes: new Dictionary<string, string>(),
            failures: new Dictionary<string, string>
            {
                ["https://example.com/api.md"] = "Connection refused",
                ["https://example.com/guide.md"] = "Timeout"
            });

        var result = await FetchSectionTool.FetchSection(
            "test.com", "Docs",
            CreateDnsFailureFetcher(), _cache, contentFetcher, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(2, root.GetProperty("fetchErrors").GetInt32());
    }

    // ===============================================================
    // Content Cleaning
    // ===============================================================

    /// <summary>
    /// HTML comments in fetched content are stripped before being returned.
    /// </summary>
    [Fact]
    public async Task FetchSectionTool_ContentWithHtmlComments_StripsComments()
    {
        await SeedCacheAsync("test.com", ValidLlmsTxt);

        var contentFetcher = new MockContentFetcher(new Dictionary<string, string>
        {
            ["https://example.com/api.md"] = "# API\n<!-- This is a comment -->\nVisible content",
            ["https://example.com/guide.md"] = "# Guide"
        });

        var result = await FetchSectionTool.FetchSection(
            "test.com", "Docs",
            CreateDnsFailureFetcher(), _cache, contentFetcher, _logger);

        using var doc = ParseResponse(result);
        var content = doc.RootElement.GetProperty("entries")[0].GetProperty("content").GetString();

        Assert.DoesNotContain("<!-- This is a comment -->", content);
        Assert.Contains("Visible content", content);
    }

    // ===============================================================
    // Domain Error Scenarios
    // ===============================================================

    /// <summary>
    /// When the domain's llms.txt returns 404, the tool returns a domain-level
    /// error (not a section-level one).
    /// </summary>
    [Fact]
    public async Task FetchSectionTool_DomainNotFound_ReturnsError()
    {
        var fetcher = CreateMockFetcher(404, "Not Found");
        var contentFetcher = new MockContentFetcher(new Dictionary<string, string>());

        var result = await FetchSectionTool.FetchSection(
            "nofile.com", "Docs", fetcher, _cache, contentFetcher, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Contains("404", root.GetProperty("errorMessage").GetString());
    }

    // ===============================================================
    // Input Validation
    // ===============================================================

    /// <summary>
    /// An empty domain parameter returns a structured error response.
    /// </summary>
    [Fact]
    public async Task FetchSectionTool_EmptyDomain_ReturnsError()
    {
        var contentFetcher = new MockContentFetcher(new Dictionary<string, string>());

        var result = await FetchSectionTool.FetchSection(
            "", "Docs",
            CreateMockFetcher(200, ValidLlmsTxt), _cache, contentFetcher, _logger);

        using var doc = ParseResponse(result);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("domain", doc.RootElement.GetProperty("errorMessage").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An empty section parameter returns a structured error response.
    /// </summary>
    [Fact]
    public async Task FetchSectionTool_EmptySection_ReturnsError()
    {
        var contentFetcher = new MockContentFetcher(new Dictionary<string, string>());

        var result = await FetchSectionTool.FetchSection(
            "test.com", "",
            CreateMockFetcher(200, ValidLlmsTxt), _cache, contentFetcher, _logger);

        using var doc = ParseResponse(result);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("section", doc.RootElement.GetProperty("errorMessage").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    // ===============================================================
    // JSON Structure Validation
    // ===============================================================

    /// <summary>
    /// The success response contains all required fields per Design Spec § 3.2.
    /// </summary>
    [Fact]
    public async Task FetchSectionTool_SuccessResponse_ContainsAllRequiredFields()
    {
        await SeedCacheAsync("test.com", ValidLlmsTxt);
        var contentFetcher = new MockContentFetcher(new Dictionary<string, string>
        {
            ["https://example.com/api.md"] = "# API",
            ["https://example.com/guide.md"] = "# Guide"
        });

        var result = await FetchSectionTool.FetchSection(
            "test.com", "Docs",
            CreateDnsFailureFetcher(), _cache, contentFetcher, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("success", out _), "Missing 'success' field");
        Assert.True(root.TryGetProperty("domain", out _), "Missing 'domain' field");
        Assert.True(root.TryGetProperty("section", out _), "Missing 'section' field");
        Assert.True(root.TryGetProperty("entryCount", out _), "Missing 'entryCount' field");
        Assert.True(root.TryGetProperty("entries", out _), "Missing 'entries' field");
        Assert.True(root.TryGetProperty("fromCache", out _), "Missing 'fromCache' field");

        // Verify entry structure
        var firstEntry = root.GetProperty("entries")[0];
        Assert.True(firstEntry.TryGetProperty("title", out _), "Entry missing 'title'");
        Assert.True(firstEntry.TryGetProperty("url", out _), "Entry missing 'url'");
    }

    /// <summary>
    /// Entries include the description from the llms.txt file when present.
    /// </summary>
    [Fact]
    public async Task FetchSectionTool_EntryWithDescription_IncludesDescription()
    {
        await SeedCacheAsync("test.com", ValidLlmsTxt);
        var contentFetcher = new MockContentFetcher(new Dictionary<string, string>
        {
            ["https://example.com/api.md"] = "# API",
            ["https://example.com/guide.md"] = "# Guide"
        });

        var result = await FetchSectionTool.FetchSection(
            "test.com", "Docs",
            CreateDnsFailureFetcher(), _cache, contentFetcher, _logger);

        using var doc = ParseResponse(result);
        var firstEntry = doc.RootElement.GetProperty("entries")[0];

        // The llms.txt has "Complete API docs" as description for the first entry
        Assert.Equal("Complete API docs", firstEntry.GetProperty("description").GetString());
    }
}

// ---------------------------------------------------------------
// MockContentFetcher — Test double for IContentFetcher
// ---------------------------------------------------------------

/// <summary>
/// A mock <see cref="IContentFetcher"/> that returns pre-configured content
/// for specific URLs, or errors for unrecognized URLs. Supports configuring
/// both successes and explicit failures.
/// </summary>
internal sealed class MockContentFetcher : IContentFetcher
{
    private readonly Dictionary<string, string> _successes;
    private readonly Dictionary<string, string> _failures;

    /// <summary>
    /// Creates a content fetcher with pre-configured success responses.
    /// URLs not in the dictionary return a "not found" error.
    /// </summary>
    public MockContentFetcher(Dictionary<string, string> successes)
    {
        _successes = successes;
        _failures = new Dictionary<string, string>();
    }

    /// <summary>
    /// Creates a content fetcher with both pre-configured successes and failures.
    /// </summary>
    public MockContentFetcher(
        Dictionary<string, string> successes,
        Dictionary<string, string> failures)
    {
        _successes = successes;
        _failures = failures;
    }

    public Task<ContentFetchResult> FetchContentAsync(
        Uri url, CancellationToken cancellationToken = default)
    {
        var urlStr = url.ToString();

        if (_successes.TryGetValue(urlStr, out var content))
            return Task.FromResult(new ContentFetchResult(content, null));

        if (_failures.TryGetValue(urlStr, out var error))
            return Task.FromResult(new ContentFetchResult(null, error));

        // Default: URL not found
        return Task.FromResult(new ContentFetchResult(null, $"URL not found in mock: {urlStr}"));
    }
}
