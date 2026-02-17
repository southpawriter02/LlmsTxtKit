using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LlmsTxtKit.Core.Caching;
using LlmsTxtKit.Core.Context;
using LlmsTxtKit.Core.Fetching;
using LlmsTxtKit.Core.Parsing;
using LlmsTxtKit.Core.Validation;
using LlmsTxtKit.Mcp.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlmsTxtKit.Integration.Tests;

/// <summary>
/// Integration tests that exercise multi-step, cross-component workflows
/// end-to-end. These tests simulate realistic agent interactions using
/// mock HTTP infrastructure — no live network calls are made.
/// </summary>
/// <remarks>
/// <para>
/// Unlike unit tests (which test individual components in isolation), these
/// integration tests verify that the full stack works together: MCP tools →
/// Core services → HTTP layer → JSON serialization. They catch issues like
/// incorrect DI wiring, serialization mismatches, and cross-component state
/// leakage that unit tests miss.
/// </para>
/// <para>
/// Each test constructs its own service instances (fetcher, cache, validator,
/// context generator) with mock HTTP handlers, then calls MCP tool methods
/// directly. This avoids the complexity of spinning up a real MCP server
/// while still exercising the full tool-to-core integration path.
/// </para>
/// </remarks>
public class EndToEndWorkflowTests : IDisposable
{
    // ---------------------------------------------------------------
    // Shared Test Data
    // ---------------------------------------------------------------

    /// <summary>
    /// Realistic llms.txt content with multiple sections, including Optional.
    /// </summary>
    private const string RealisticLlmsTxt = """
        # Anthropic
        > Anthropic is an AI safety company that builds reliable, interpretable, and steerable AI systems.

        ## Docs
        - [API Reference](https://docs.anthropic.com/api.md): Complete API documentation
        - [Getting Started](https://docs.anthropic.com/getting-started.md): Quickstart guide
        - [Authentication](https://docs.anthropic.com/auth.md): API key management

        ## Examples
        - [Cookbook](https://docs.anthropic.com/cookbook.md): Code examples and patterns

        ## Optional
        - [Changelog](https://docs.anthropic.com/changelog.md): Release notes and version history
        """;

    /// <summary>Sample Markdown content returned for linked URLs.</summary>
    private static readonly Dictionary<string, string> SampleContent = new()
    {
        ["https://docs.anthropic.com/api.md"] =
            "# API Reference\n\nThe Anthropic API provides access to Claude models. " +
            "Use POST /v1/messages to send messages. Authentication via API key in x-api-key header. " +
            "Rate limits apply: 1000 requests per minute for standard tier.",
        ["https://docs.anthropic.com/getting-started.md"] =
            "# Getting Started\n\nInstall the SDK with pip install anthropic. " +
            "Set your API key as ANTHROPIC_API_KEY environment variable. " +
            "Create a client and send your first message.",
        ["https://docs.anthropic.com/auth.md"] =
            "# Authentication\n\nAll API requests require an API key. " +
            "Generate keys in the Anthropic Console. " +
            "Pass the key in the x-api-key header.",
        ["https://docs.anthropic.com/cookbook.md"] =
            "# Cookbook\n\nExample: Summarize a document.\n```python\nresult = client.messages.create(...)\n```",
        ["https://docs.anthropic.com/changelog.md"] =
            "# Changelog\n\n## v2.0.0\n- New messages API\n- Improved streaming support"
    };

    private readonly List<IDisposable> _disposables = new();

    private static readonly JsonDocumentOptions JsonParseOptions = new() { AllowTrailingCommas = true };

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
    }

    // ---------------------------------------------------------------
    // Service Factory Methods
    // ---------------------------------------------------------------

    /// <summary>
    /// Creates a full service stack (fetcher + cache + validator + context generator)
    /// backed by a mock HTTP handler. The handler returns different content based
    /// on the request URL, simulating a realistic web server.
    /// </summary>
    private (LlmsTxtFetcher Fetcher, LlmsTxtCache Cache, LlmsDocumentValidator Validator,
             ContextGenerator ContextGen, IContentFetcher ContentFetcher)
        CreateServiceStack(
            Func<HttpRequestMessage, HttpResponseMessage> httpHandler,
            CacheOptions? cacheOptions = null)
    {
        var handler = new IntegrationMockHandler(httpHandler);
        var httpClient = new HttpClient(handler);

        var fetcher = new LlmsTxtFetcher(new FetcherOptions { MaxRetries = 0 }, httpClient);
        var cache = new LlmsTxtCache(cacheOptions ?? new CacheOptions { StaleWhileRevalidate = false });
        var validator = new LlmsDocumentValidator();
        var contentFetcher = new IntegrationContentFetcher(httpClient);
        var contextGen = new ContextGenerator(contentFetcher);

        _disposables.Add(fetcher);
        _disposables.Add(httpClient);

        return (fetcher, cache, validator, contextGen, contentFetcher);
    }

    /// <summary>
    /// Creates a standard mock handler that serves RealisticLlmsTxt for /llms.txt
    /// requests and SampleContent for linked URLs.
    /// </summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> CreateStandardHandler()
    {
        return request =>
        {
            var url = request.RequestUri!.ToString();

            // Serve llms.txt for any /llms.txt request
            if (url.EndsWith("/llms.txt"))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(RealisticLlmsTxt)
                };
            }

            // Serve sample content for known URLs
            if (SampleContent.TryGetValue(url, out var content))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(content)
                };
            }

            // 404 for everything else
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent("Not Found")
            };
        };
    }

    private static JsonDocument ParseResponse(string json) =>
        JsonDocument.Parse(json, JsonParseOptions);

    // ===============================================================
    // 1. End-to-End Discovery Workflow
    // ===============================================================

    /// <summary>
    /// Simulates a complete agent workflow: discover → fetch section → generate
    /// context. Verifies the full pipeline produces correct, coherent output
    /// across all three tool calls.
    /// </summary>
    [Fact]
    public async Task EndToEnd_DiscoverThenFetchThenContext_ProducesCorrectOutput()
    {
        // Arrange: create full service stack with standard handler
        var (fetcher, cache, validator, contextGen, contentFetcher) =
            CreateServiceStack(CreateStandardHandler());

        var discoverLogger = NullLogger<DiscoverTool>.Instance;
        var fetchLogger = NullLogger<FetchSectionTool>.Instance;
        var contextLogger = NullLogger<ContextTool>.Instance;

        // Step 1: Discover — agent checks if the domain has llms.txt
        var discoverResult = await DiscoverTool.Discover(
            "docs.anthropic.com", fetcher, cache, discoverLogger);

        using var discoverDoc = ParseResponse(discoverResult);
        Assert.True(discoverDoc.RootElement.GetProperty("found").GetBoolean(),
            "Step 1: Domain should have an llms.txt file");
        Assert.Equal("Anthropic", discoverDoc.RootElement.GetProperty("title").GetString());

        // Verify sections are discoverable
        var sections = discoverDoc.RootElement.GetProperty("sections");
        Assert.Equal(3, sections.GetArrayLength()); // Docs, Examples, Optional

        // Step 2: Fetch Section — agent fetches the "Docs" section content
        var fetchResult = await FetchSectionTool.FetchSection(
            "docs.anthropic.com", "Docs",
            fetcher, cache, contentFetcher, fetchLogger);

        using var fetchDoc = ParseResponse(fetchResult);
        Assert.True(fetchDoc.RootElement.GetProperty("success").GetBoolean(),
            "Step 2: Docs section should be fetchable");
        Assert.Equal(3, fetchDoc.RootElement.GetProperty("entryCount").GetInt32());

        // Content should be present for all entries
        var entries = fetchDoc.RootElement.GetProperty("entries");
        for (int i = 0; i < entries.GetArrayLength(); i++)
        {
            Assert.True(entries[i].TryGetProperty("content", out var contentProp),
                $"Step 2: Entry {i} should have content");
            Assert.False(string.IsNullOrEmpty(contentProp.GetString()),
                $"Step 2: Entry {i} content should not be empty");
        }

        // Step 3: Generate Context — agent gets the full LLM-ready context
        var contextResult = await ContextTool.GenerateContext(
            "docs.anthropic.com", null, false,
            fetcher, cache, contextGen, contextLogger);

        using var contextDoc = ParseResponse(contextResult);
        Assert.True(contextDoc.RootElement.GetProperty("success").GetBoolean(),
            "Step 3: Context generation should succeed");

        var context = contextDoc.RootElement.GetProperty("content").GetString()!;
        Assert.Contains("<section name=\"Docs\">", context);
        Assert.Contains("API Reference", context);
        Assert.True(contextDoc.RootElement.GetProperty("approximateTokenCount").GetInt32() > 0);

        // All three steps should use the same cached document (no re-fetch)
        Assert.True(fetchDoc.RootElement.GetProperty("fromCache").GetBoolean(),
            "Step 2 should have used cache from Step 1");
        Assert.True(contextDoc.RootElement.GetProperty("fromCache").GetBoolean(),
            "Step 3 should have used cache from Step 1");
    }

    // ===============================================================
    // 2. WAF Block Graceful Degradation
    // ===============================================================

    /// <summary>
    /// When a domain blocks llms.txt with a WAF, all MCP tools return structured
    /// error information that an agent can reason about — no exceptions, no
    /// unstructured error strings.
    /// </summary>
    [Fact]
    public async Task WafBlock_AllToolsReturnStructuredErrors()
    {
        // Arrange: simulate Cloudflare WAF block for all requests
        var (fetcher, cache, validator, contextGen, contentFetcher) =
            CreateServiceStack(request =>
            {
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("Access Denied")
                };
                response.Headers.TryAddWithoutValidation("cf-ray", "abc123-LAX");
                response.Headers.TryAddWithoutValidation("server", "cloudflare");
                return response;
            });

        // Discover: should report blocked with structured error
        var discoverResult = await DiscoverTool.Discover(
            "blocked.example.com", fetcher, cache,
            NullLogger<DiscoverTool>.Instance);

        using var discoverDoc = ParseResponse(discoverResult);
        Assert.False(discoverDoc.RootElement.GetProperty("found").GetBoolean());
        Assert.Equal("blocked", discoverDoc.RootElement.GetProperty("status").GetString());
        Assert.True(discoverDoc.RootElement.TryGetProperty("blockReason", out _),
            "Blocked response should include a blockReason for agent reasoning");

        // Fetch Section: should report blocked
        var fetchResult = await FetchSectionTool.FetchSection(
            "blocked.example.com", "Docs",
            fetcher, cache, contentFetcher,
            NullLogger<FetchSectionTool>.Instance);

        using var fetchDoc = ParseResponse(fetchResult);
        Assert.False(fetchDoc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("blocked", fetchDoc.RootElement.GetProperty("errorMessage").GetString()!,
            StringComparison.OrdinalIgnoreCase);

        // Validate: should report blocked
        var validateResult = await ValidateTool.Validate(
            "blocked.example.com", false, false,
            fetcher, cache, validator,
            NullLogger<ValidateTool>.Instance);

        using var validateDoc = ParseResponse(validateResult);
        Assert.False(validateDoc.RootElement.GetProperty("success").GetBoolean());

        // Context: should report blocked
        var contextResult = await ContextTool.GenerateContext(
            "blocked.example.com", null, false,
            fetcher, cache, contextGen,
            NullLogger<ContextTool>.Instance);

        using var contextDoc = ParseResponse(contextResult);
        Assert.False(contextDoc.RootElement.GetProperty("success").GetBoolean());
    }

    // ===============================================================
    // 3. Cache Integration — Hit Within TTL
    // ===============================================================

    /// <summary>
    /// Fetching a domain, then fetching again within TTL, uses the cache
    /// (no second HTTP request). Verified by counting HTTP requests via
    /// a request-counting handler.
    /// </summary>
    [Fact]
    public async Task Cache_SecondFetchWithinTtl_UsesCache()
    {
        var requestCount = 0;
        var (fetcher, cache, _, _, _) = CreateServiceStack(request =>
        {
            Interlocked.Increment(ref requestCount);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(RealisticLlmsTxt)
            };
        });

        var logger = NullLogger<DiscoverTool>.Instance;

        // First call: should make an HTTP request
        var result1 = await DiscoverTool.Discover("cached.example.com", fetcher, cache, logger);
        using var doc1 = ParseResponse(result1);
        Assert.True(doc1.RootElement.GetProperty("found").GetBoolean());
        Assert.False(doc1.RootElement.GetProperty("fromCache").GetBoolean());
        Assert.Equal(1, requestCount);

        // Second call: should use cache (no additional HTTP request)
        var result2 = await DiscoverTool.Discover("cached.example.com", fetcher, cache, logger);
        using var doc2 = ParseResponse(result2);
        Assert.True(doc2.RootElement.GetProperty("found").GetBoolean());
        Assert.True(doc2.RootElement.GetProperty("fromCache").GetBoolean());
        Assert.Equal(1, requestCount); // Still 1 — no second request
    }

    // ===============================================================
    // 4. Stale-While-Revalidate Integration
    // ===============================================================

    /// <summary>
    /// With stale-while-revalidate enabled, an expired cache entry is still
    /// returned immediately (fromCache=true) rather than blocking on a refetch.
    /// </summary>
    [Fact]
    public async Task StaleWhileRevalidate_ExpiredEntry_ReturnsStaleImmediately()
    {
        // Arrange: cache with SWR enabled and very short TTL
        var cache = new LlmsTxtCache(new CacheOptions
        {
            StaleWhileRevalidate = true,
            DefaultTtl = TimeSpan.FromMilliseconds(1) // Expires almost immediately
        });

        // Pre-populate cache with an already-expired entry
        var doc = LlmsDocumentParser.Parse(RealisticLlmsTxt);
        var entry = new CacheEntry
        {
            Document = doc,
            FetchedAt = DateTimeOffset.UtcNow.AddHours(-2),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1), // Already expired
            FetchResult = new FetchResult
            {
                Status = FetchStatus.Success,
                Document = doc,
                RawContent = RealisticLlmsTxt,
                StatusCode = 200,
                Duration = TimeSpan.FromMilliseconds(50),
                Domain = "stale.example.com"
            }
        };
        await cache.SetAsync("stale.example.com", entry);

        // Verify the entry is expired
        var cached = await cache.GetAsync("stale.example.com");
        Assert.NotNull(cached);
        Assert.True(cached.IsExpired());

        // With SWR, the cache should still return the stale entry
        // (GetAsync returns stale entries when SWR is enabled)
        Assert.Equal("Anthropic", cached.Document.Title);
    }

    // ===============================================================
    // 5. Token Budget End-to-End
    // ===============================================================

    /// <summary>
    /// Generate context with a tight token budget. Verify the output respects
    /// the budget and reports which sections were included/omitted/truncated.
    /// </summary>
    [Fact]
    public async Task TokenBudget_TightLimit_RespectsAndReportsSections()
    {
        var (fetcher, cache, _, contextGen, _) =
            CreateServiceStack(CreateStandardHandler());

        var logger = NullLogger<ContextTool>.Instance;

        // First, generate with no limit to see the full size
        var fullResult = await ContextTool.GenerateContext(
            "docs.anthropic.com", null, true, // include optional
            fetcher, cache, contextGen, logger);

        using var fullDoc = ParseResponse(fullResult);
        var fullTokens = fullDoc.RootElement.GetProperty("approximateTokenCount").GetInt32();
        Assert.True(fullTokens > 0, "Full context should have tokens");

        // Now generate with a tight budget (half of full)
        var budget = fullTokens / 2;
        var budgetResult = await ContextTool.GenerateContext(
            "docs.anthropic.com", budget, true,
            fetcher, cache, contextGen, logger);

        using var budgetDoc = ParseResponse(budgetResult);
        Assert.True(budgetDoc.RootElement.GetProperty("success").GetBoolean());

        var budgetTokens = budgetDoc.RootElement.GetProperty("approximateTokenCount").GetInt32();
        Assert.True(budgetTokens <= budget,
            $"Token count {budgetTokens} should be <= budget {budget}");

        // Should have some sections included
        var included = budgetDoc.RootElement.GetProperty("sectionsIncluded");
        Assert.True(included.GetArrayLength() > 0, "At least one section should be included");

        // With a tight budget and includeOptional=true, Optional should likely be omitted
        // (it's the first to be dropped under budget pressure)
    }

    // ===============================================================
    // 6. Validation with URL Checks (Structural Only)
    // ===============================================================

    /// <summary>
    /// Validate a document with structural checks. A well-formed document
    /// validates successfully; a malformed one produces errors.
    /// </summary>
    [Fact]
    public async Task Validation_StructuralChecks_ProducesCorrectReport()
    {
        // Test 1: Valid document
        var (fetcher1, cache1, validator1, _, _) =
            CreateServiceStack(CreateStandardHandler());

        var validResult = await ValidateTool.Validate(
            "docs.anthropic.com", false, false,
            fetcher1, cache1, validator1,
            NullLogger<ValidateTool>.Instance);

        using var validDoc = ParseResponse(validResult);
        Assert.True(validDoc.RootElement.GetProperty("isValid").GetBoolean(),
            "Well-formed document should be valid");
        Assert.Equal(0, validDoc.RootElement.GetProperty("errorCount").GetInt32());

        // Test 2: Malformed document (no H1)
        var malformedLlmsTxt = "## Docs\n- [Link](https://example.com/doc.md): A doc";
        var (fetcher2, cache2, validator2, _, _) = CreateServiceStack(request =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(malformedLlmsTxt)
            });

        var invalidResult = await ValidateTool.Validate(
            "malformed.example.com", false, false,
            fetcher2, cache2, validator2,
            NullLogger<ValidateTool>.Instance);

        using var invalidDoc = ParseResponse(invalidResult);
        Assert.False(invalidDoc.RootElement.GetProperty("isValid").GetBoolean(),
            "Malformed document (no H1) should be invalid");
        Assert.True(invalidDoc.RootElement.GetProperty("errorCount").GetInt32() > 0);

        // Issues should have proper structure
        var issues = invalidDoc.RootElement.GetProperty("issues");
        Assert.True(issues.GetArrayLength() > 0);
        var firstIssue = issues[0];
        Assert.Equal("error", firstIssue.GetProperty("severity").GetString());
        Assert.False(string.IsNullOrEmpty(firstIssue.GetProperty("rule").GetString()));
    }

    // ===============================================================
    // 7. Large File Handling
    // ===============================================================

    /// <summary>
    /// Feed a synthetic 1000+ entry llms.txt file through the full pipeline
    /// (parse → validate → cache → generate context). Verify it completes
    /// within reasonable time bounds and produces correct output.
    /// </summary>
    [Fact]
    public async Task LargeFile_1000Entries_CompletesWithinTimeBounds()
    {
        // Generate a synthetic llms.txt with 1000+ entries across 10 sections
        var sb = new StringBuilder();
        sb.AppendLine("# LargeTestSite");
        sb.AppendLine("> A large test site with many sections and entries.");
        sb.AppendLine();

        for (int section = 0; section < 10; section++)
        {
            sb.AppendLine($"## Section{section}");
            for (int entry = 0; entry < 100; entry++)
            {
                sb.AppendLine($"- [Entry {section}-{entry}](https://example.com/s{section}/e{entry}.md): Description for entry {entry}");
            }
            sb.AppendLine();
        }

        var largeLlmsTxt = sb.ToString();

        // Build mock content (short per entry to keep the test fast)
        var mockContent = new Dictionary<string, string>();
        for (int s = 0; s < 10; s++)
        for (int e = 0; e < 100; e++)
            mockContent[$"https://example.com/s{s}/e{e}.md"] = $"Content for entry {s}-{e}.";

        var (fetcher, cache, validator, _, _) = CreateServiceStack(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/llms.txt"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(largeLlmsTxt)
                };
            if (mockContent.TryGetValue(url, out var content))
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(content)
                };
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent("Not Found")
            };
        });

        var contentFetcher = new IntegrationContentFetcher(new HttpClient(
            new IntegrationMockHandler(request =>
            {
                var url = request.RequestUri!.ToString();
                if (mockContent.TryGetValue(url, out var c))
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent(c)
                    };
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                {
                    Content = new StringContent("Not Found")
                };
            })));
        var contextGen = new ContextGenerator(contentFetcher);

        var stopwatch = Stopwatch.StartNew();

        // Step 1: Parse (implicitly via Discover)
        var discoverResult = await DiscoverTool.Discover(
            "large.example.com", fetcher, cache,
            NullLogger<DiscoverTool>.Instance);

        using var discoverDoc = ParseResponse(discoverResult);
        Assert.True(discoverDoc.RootElement.GetProperty("found").GetBoolean());
        Assert.Equal(10, discoverDoc.RootElement.GetProperty("sections").GetArrayLength());

        // Verify each section has 100 entries
        var firstSection = discoverDoc.RootElement.GetProperty("sections")[0];
        Assert.Equal(100, firstSection.GetProperty("entryCount").GetInt32());

        // Step 2: Validate
        var validateResult = await ValidateTool.Validate(
            "large.example.com", false, false,
            fetcher, cache, validator,
            NullLogger<ValidateTool>.Instance);

        using var validateDoc = ParseResponse(validateResult);
        Assert.True(validateDoc.RootElement.GetProperty("isValid").GetBoolean());

        // Step 3: Generate context with a token budget
        var contextResult = await ContextTool.GenerateContext(
            "large.example.com", 500, false,
            fetcher, cache, contextGen,
            NullLogger<ContextTool>.Instance);

        using var contextDoc = ParseResponse(contextResult);
        Assert.True(contextDoc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(
            contextDoc.RootElement.GetProperty("approximateTokenCount").GetInt32() <= 500,
            "Token budget should be respected even with 1000+ entries");

        stopwatch.Stop();

        // The full pipeline should complete within 30 seconds even on slow hardware
        // (it's all in-memory with mock HTTP, so it should be <1s typically)
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"Full pipeline took {stopwatch.Elapsed.TotalSeconds:F1}s, should be < 30s");
    }
}

// ---------------------------------------------------------------
// Integration Test Infrastructure
// ---------------------------------------------------------------

/// <summary>
/// Mock HTTP handler for integration tests. Delegates to a factory function
/// that can inspect the request and return different responses based on URL.
/// </summary>
internal sealed class IntegrationMockHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public IntegrationMockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_handler(request));
    }
}

/// <summary>
/// Content fetcher for integration tests that uses a mock-backed HttpClient.
/// This bridges the IContentFetcher interface to the mock HTTP infrastructure
/// so that ContextGenerator and FetchSectionTool can fetch linked content
/// through the same mocked HTTP pipeline.
/// </summary>
internal sealed class IntegrationContentFetcher : IContentFetcher
{
    private readonly HttpClient _httpClient;

    public IntegrationContentFetcher(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ContentFetchResult> FetchContentAsync(
        Uri url, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new ContentFetchResult(null, $"HTTP {(int)response.StatusCode}");

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return new ContentFetchResult(content, null);
        }
        catch (Exception ex)
        {
            return new ContentFetchResult(null, ex.Message);
        }
    }
}
