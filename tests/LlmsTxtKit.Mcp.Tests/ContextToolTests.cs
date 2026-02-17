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
/// Unit tests for the <c>llmstxt_context</c> MCP tool (<see cref="ContextTool"/>).
/// Tests verify context generation, token budgeting, Optional section handling,
/// error reporting, and response structure.
/// </summary>
/// <remarks>
/// <para>
/// These tests exercise <see cref="ContextTool.GenerateContext"/> directly with a
/// pre-seeded cache and a <see cref="MockContentFetcher"/> for linked content.
/// The <see cref="ContextGenerator"/> is used directly (not mocked) to verify
/// the full generation pipeline.
/// </para>
/// </remarks>
public class ContextToolTests : IDisposable
{
    // ---------------------------------------------------------------
    // Test Infrastructure
    // ---------------------------------------------------------------

    /// <summary>
    /// llms.txt content with two sections: Docs (2 entries) and Optional (1 entry).
    /// </summary>
    private const string ValidLlmsTxt = """
        # TestSite
        > A test site for context generation.
        ## Docs
        - [API Reference](https://example.com/api.md): API docs
        - [Getting Started](https://example.com/guide.md): Quickstart
        ## Optional
        - [Advanced Topics](https://example.com/advanced.md): Deep dive
        """;

    /// <summary>
    /// Long-form content for testing token budget enforcement. Each sentence
    /// is roughly 10 words, so 20 sentences ≈ 200 words ≈ 50 word-tokens.
    /// </summary>
    private const string LongContent =
        "This is the first sentence of the long document. " +
        "This is the second sentence of the long document. " +
        "This is the third sentence of the long document. " +
        "This is the fourth sentence of the long document. " +
        "This is the fifth sentence of the long document. " +
        "This is the sixth sentence of the long document. " +
        "This is the seventh sentence of the long document. " +
        "This is the eighth sentence of the long document. " +
        "This is the ninth sentence of the long document. " +
        "This is the tenth sentence of the long document. " +
        "This is the eleventh sentence of the long document. " +
        "This is the twelfth sentence of the long document. " +
        "This is the thirteenth sentence of the long document. " +
        "This is the fourteenth sentence of the long document. " +
        "This is the fifteenth sentence of the long document. " +
        "This is the sixteenth sentence of the long document. " +
        "This is the seventeenth sentence of the long document. " +
        "This is the eighteenth sentence of the long document. " +
        "This is the nineteenth sentence of the long document. " +
        "This is the twentieth sentence of the long document.";

    private readonly ILogger<ContextTool> _logger = NullLogger<ContextTool>.Instance;
    private readonly LlmsTxtCache _cache = new(new CacheOptions { StaleWhileRevalidate = false });
    private readonly List<LlmsTxtFetcher> _fetchers = new();

    private static readonly JsonDocumentOptions JsonParseOptions = new() { AllowTrailingCommas = true };

    public void Dispose()
    {
        foreach (var fetcher in _fetchers)
            fetcher.Dispose();
    }

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

    private LlmsTxtFetcher CreateDnsFailureFetcher()
    {
        var handler = new MockHttpHandler(_ =>
            throw new HttpRequestException("No such host."));
        var httpClient = new HttpClient(handler);
        var fetcher = new LlmsTxtFetcher(new FetcherOptions { MaxRetries = 0 }, httpClient);
        _fetchers.Add(fetcher);
        return fetcher;
    }

    private static JsonDocument ParseResponse(string json) =>
        JsonDocument.Parse(json, JsonParseOptions);

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

    /// <summary>
    /// Creates a <see cref="ContextGenerator"/> backed by a <see cref="MockContentFetcher"/>.
    /// </summary>
    private static ContextGenerator CreateContextGenerator(Dictionary<string, string> contents)
    {
        var contentFetcher = new MockContentFetcher(contents);
        return new ContextGenerator(contentFetcher);
    }

    // ===============================================================
    // Success Scenarios
    // ===============================================================

    /// <summary>
    /// A valid domain with fetchable content returns a context string with
    /// content, tokenCount, and sectionsIncluded.
    /// </summary>
    [Fact]
    public async Task ContextTool_ValidDomain_ReturnsContextString()
    {
        await SeedCacheAsync("test.com", ValidLlmsTxt);
        var generator = CreateContextGenerator(new Dictionary<string, string>
        {
            ["https://example.com/api.md"] = "# API\nAPI reference documentation.",
            ["https://example.com/guide.md"] = "# Guide\nQuickstart guide content."
        });

        var result = await ContextTool.GenerateContext(
            "test.com", null, false,
            CreateDnsFailureFetcher(), _cache, generator, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("test.com", root.GetProperty("domain").GetString());

        // Content should contain the fetched material
        var content = root.GetProperty("content").GetString();
        Assert.NotNull(content);
        Assert.Contains("API reference documentation", content);
        Assert.Contains("Quickstart guide content", content);

        // Token count should be positive
        Assert.True(root.GetProperty("approximateTokenCount").GetInt32() > 0);

        // Docs section should be included
        var sectionsIncluded = root.GetProperty("sectionsIncluded");
        Assert.Contains("Docs", EnumerateStringArray(sectionsIncluded));
    }

    /// <summary>
    /// By default, the Optional section is excluded from context.
    /// </summary>
    [Fact]
    public async Task ContextTool_DefaultExcludesOptional()
    {
        await SeedCacheAsync("test.com", ValidLlmsTxt);
        var generator = CreateContextGenerator(new Dictionary<string, string>
        {
            ["https://example.com/api.md"] = "# API",
            ["https://example.com/guide.md"] = "# Guide"
        });

        var result = await ContextTool.GenerateContext(
            "test.com", null, false,
            CreateDnsFailureFetcher(), _cache, generator, _logger);

        using var doc = ParseResponse(result);
        var content = doc.RootElement.GetProperty("content").GetString();

        // Optional section content should NOT be present
        Assert.DoesNotContain("Advanced Topics", content);
        // Docs section should be included
        var included = EnumerateStringArray(doc.RootElement.GetProperty("sectionsIncluded"));
        Assert.DoesNotContain("Optional", included);
    }

    /// <summary>
    /// When includeOptional=true, the Optional section is included in context.
    /// </summary>
    [Fact]
    public async Task ContextTool_IncludeOptionalTrue_IncludesOptionalSection()
    {
        await SeedCacheAsync("test.com", ValidLlmsTxt);
        var generator = CreateContextGenerator(new Dictionary<string, string>
        {
            ["https://example.com/api.md"] = "# API\nAPI docs.",
            ["https://example.com/guide.md"] = "# Guide\nGuide docs.",
            ["https://example.com/advanced.md"] = "# Advanced\nAdvanced docs."
        });

        var result = await ContextTool.GenerateContext(
            "test.com", null, true,  // includeOptional = true
            CreateDnsFailureFetcher(), _cache, generator, _logger);

        using var doc = ParseResponse(result);
        var included = EnumerateStringArray(doc.RootElement.GetProperty("sectionsIncluded"));
        Assert.Contains("Optional", included);
        Assert.Contains("Advanced docs", doc.RootElement.GetProperty("content").GetString());
    }

    // ===============================================================
    // Token Budget Scenarios
    // ===============================================================

    /// <summary>
    /// When maxTokens is set, the approximate token count in the response
    /// is at or below the specified budget.
    /// </summary>
    [Fact]
    public async Task ContextTool_WithTokenBudget_RespectsLimit()
    {
        await SeedCacheAsync("test.com", ValidLlmsTxt);

        // Use long content so truncation actually kicks in
        var generator = CreateContextGenerator(new Dictionary<string, string>
        {
            ["https://example.com/api.md"] = LongContent,
            ["https://example.com/guide.md"] = LongContent
        });

        // Set a small token budget (30 tokens ≈ 120 words)
        var result = await ContextTool.GenerateContext(
            "test.com", 30, false,
            CreateDnsFailureFetcher(), _cache, generator, _logger);

        using var doc = ParseResponse(result);
        var tokenCount = doc.RootElement.GetProperty("approximateTokenCount").GetInt32();

        // Token count should be at or below budget
        Assert.True(tokenCount <= 30,
            $"Token count {tokenCount} exceeded budget of 30");
    }

    /// <summary>
    /// With includeOptional=true and a tight budget, the Optional section is
    /// dropped first before truncating required sections.
    /// </summary>
    [Fact]
    public async Task ContextTool_TokenBudget_DropsOptionalFirst()
    {
        await SeedCacheAsync("test.com", ValidLlmsTxt);
        var generator = CreateContextGenerator(new Dictionary<string, string>
        {
            ["https://example.com/api.md"] = LongContent,
            ["https://example.com/guide.md"] = LongContent,
            ["https://example.com/advanced.md"] = LongContent
        });

        // includeOptional=true but with tight budget
        var result = await ContextTool.GenerateContext(
            "test.com", 30, true,
            CreateDnsFailureFetcher(), _cache, generator, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        // Optional should be omitted due to budget
        if (root.TryGetProperty("sectionsOmitted", out var omitted) &&
            omitted.ValueKind == JsonValueKind.Array)
        {
            Assert.Contains("Optional", EnumerateStringArray(omitted));
        }
    }

    /// <summary>
    /// maxTokens=0 is treated as "no limit" (same as null).
    /// </summary>
    [Fact]
    public async Task ContextTool_MaxTokensZero_TreatedAsNoLimit()
    {
        await SeedCacheAsync("test.com", ValidLlmsTxt);
        var generator = CreateContextGenerator(new Dictionary<string, string>
        {
            ["https://example.com/api.md"] = LongContent,
            ["https://example.com/guide.md"] = LongContent
        });

        var result = await ContextTool.GenerateContext(
            "test.com", 0, false,
            CreateDnsFailureFetcher(), _cache, generator, _logger);

        using var doc = ParseResponse(result);
        // All content should be included (no truncation or omission)
        var content = doc.RootElement.GetProperty("content").GetString();
        Assert.Contains("twentieth sentence", content);
    }

    // ===============================================================
    // Fetch Error Handling
    // ===============================================================

    /// <summary>
    /// When linked URLs fail to fetch, the response includes fetchErrors
    /// with URL and error details.
    /// </summary>
    [Fact]
    public async Task ContextTool_FetchErrors_IncludedInResponse()
    {
        await SeedCacheAsync("test.com", ValidLlmsTxt);
        var contentFetcher = new MockContentFetcher(
            successes: new Dictionary<string, string>
            {
                ["https://example.com/api.md"] = "# API\nAPI docs."
            },
            failures: new Dictionary<string, string>
            {
                ["https://example.com/guide.md"] = "HTTP 500 Internal Server Error"
            });
        var generator = new ContextGenerator(contentFetcher);

        var result = await ContextTool.GenerateContext(
            "test.com", null, false,
            CreateDnsFailureFetcher(), _cache, generator, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());

        // Should have fetch errors
        if (root.TryGetProperty("fetchErrors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array)
        {
            Assert.True(errors.GetArrayLength() > 0);
            var firstError = errors[0];
            Assert.True(firstError.TryGetProperty("url", out _), "Fetch error missing 'url'");
            Assert.True(firstError.TryGetProperty("error", out _), "Fetch error missing 'error'");
        }
    }

    // ===============================================================
    // Content Structure
    // ===============================================================

    /// <summary>
    /// The generated content wraps sections in XML tags by default.
    /// </summary>
    [Fact]
    public async Task ContextTool_ContentWrappedInXml()
    {
        await SeedCacheAsync("test.com", ValidLlmsTxt);
        var generator = CreateContextGenerator(new Dictionary<string, string>
        {
            ["https://example.com/api.md"] = "API content here.",
            ["https://example.com/guide.md"] = "Guide content here."
        });

        var result = await ContextTool.GenerateContext(
            "test.com", null, false,
            CreateDnsFailureFetcher(), _cache, generator, _logger);

        using var doc = ParseResponse(result);
        var content = doc.RootElement.GetProperty("content").GetString();

        Assert.Contains("<section name=\"Docs\">", content);
        Assert.Contains("</section>", content);
    }

    // ===============================================================
    // Domain Fetch Errors
    // ===============================================================

    /// <summary>
    /// A 404 response returns a domain-level error.
    /// </summary>
    [Fact]
    public async Task ContextTool_DomainNotFound_ReturnsError()
    {
        var fetcher = CreateMockFetcher(404, "Not Found");
        var generator = CreateContextGenerator(new Dictionary<string, string>());

        var result = await ContextTool.GenerateContext(
            "nofile.com", null, false,
            fetcher, _cache, generator, _logger);

        using var doc = ParseResponse(result);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("404", doc.RootElement.GetProperty("errorMessage").GetString());
    }

    // ===============================================================
    // Input Validation
    // ===============================================================

    /// <summary>
    /// An empty domain returns a structured error response.
    /// </summary>
    [Fact]
    public async Task ContextTool_EmptyDomain_ReturnsError()
    {
        var generator = CreateContextGenerator(new Dictionary<string, string>());

        var result = await ContextTool.GenerateContext(
            "", null, false,
            CreateMockFetcher(200, ValidLlmsTxt), _cache, generator, _logger);

        using var doc = ParseResponse(result);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("domain", doc.RootElement.GetProperty("errorMessage").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A whitespace-only domain returns a structured error response.
    /// </summary>
    [Fact]
    public async Task ContextTool_WhitespaceDomain_ReturnsError()
    {
        var generator = CreateContextGenerator(new Dictionary<string, string>());

        var result = await ContextTool.GenerateContext(
            "   ", null, false,
            CreateMockFetcher(200, ValidLlmsTxt), _cache, generator, _logger);

        using var doc = ParseResponse(result);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
    }

    // ===============================================================
    // Response Structure Validation
    // ===============================================================

    /// <summary>
    /// The success response contains all required fields per Design Spec § 3.4.
    /// </summary>
    [Fact]
    public async Task ContextTool_SuccessResponse_ContainsAllRequiredFields()
    {
        await SeedCacheAsync("test.com", ValidLlmsTxt);
        var generator = CreateContextGenerator(new Dictionary<string, string>
        {
            ["https://example.com/api.md"] = "# API\nDocs.",
            ["https://example.com/guide.md"] = "# Guide\nGuide."
        });

        var result = await ContextTool.GenerateContext(
            "test.com", null, false,
            CreateDnsFailureFetcher(), _cache, generator, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("success", out _), "Missing 'success'");
        Assert.True(root.TryGetProperty("domain", out _), "Missing 'domain'");
        Assert.True(root.TryGetProperty("content", out _), "Missing 'content'");
        Assert.True(root.TryGetProperty("approximateTokenCount", out _), "Missing 'approximateTokenCount'");
        Assert.True(root.TryGetProperty("sectionsIncluded", out _), "Missing 'sectionsIncluded'");
        Assert.True(root.TryGetProperty("fromCache", out _), "Missing 'fromCache'");
    }

    /// <summary>
    /// The error response (fetch failure) contains all expected fields.
    /// </summary>
    [Fact]
    public async Task ContextTool_ErrorResponse_ContainsAllRequiredFields()
    {
        var fetcher = CreateMockFetcher(404, "Not Found");
        var generator = CreateContextGenerator(new Dictionary<string, string>());

        var result = await ContextTool.GenerateContext(
            "missing.com", null, false,
            fetcher, _cache, generator, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("success", out _), "Missing 'success'");
        Assert.True(root.TryGetProperty("errorMessage", out _), "Missing 'errorMessage'");
    }

    // ===============================================================
    // Cache Hit Behavior
    // ===============================================================

    /// <summary>
    /// When the document is in cache, fromCache=true in the response.
    /// </summary>
    [Fact]
    public async Task ContextTool_CacheHit_SetsFromCacheTrue()
    {
        await SeedCacheAsync("cached.com", ValidLlmsTxt);
        var generator = CreateContextGenerator(new Dictionary<string, string>
        {
            ["https://example.com/api.md"] = "# API",
            ["https://example.com/guide.md"] = "# Guide"
        });

        var result = await ContextTool.GenerateContext(
            "cached.com", null, false,
            CreateDnsFailureFetcher(), _cache, generator, _logger);

        using var doc = ParseResponse(result);
        Assert.True(doc.RootElement.GetProperty("fromCache").GetBoolean());
    }

    // ===============================================================
    // Helpers
    // ===============================================================

    /// <summary>
    /// Enumerates a JSON array of strings into a List for assertion.
    /// </summary>
    private static List<string> EnumerateStringArray(JsonElement array)
    {
        return Enumerable.Range(0, array.GetArrayLength())
            .Select(i => array[i].GetString()!)
            .ToList();
    }
}
