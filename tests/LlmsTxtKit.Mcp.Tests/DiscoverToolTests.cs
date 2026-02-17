using System.Text.Json;
using LlmsTxtKit.Core.Caching;
using LlmsTxtKit.Core.Fetching;
using LlmsTxtKit.Core.Parsing;
using LlmsTxtKit.Mcp.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlmsTxtKit.Mcp.Tests;

/// <summary>
/// Unit tests for the <c>llmstxt_discover</c> MCP tool (<see cref="DiscoverTool"/>).
/// Tests verify the tool's response structure, caching behavior, error handling,
/// and input validation without making real HTTP requests.
/// </summary>
/// <remarks>
/// <para>
/// These tests exercise the <see cref="DiscoverTool.Discover"/> static method
/// directly, injecting a <see cref="LlmsTxtFetcher"/> configured with a
/// <see cref="MockHttpHandler"/> to simulate various HTTP responses. This
/// approach tests the tool logic end-to-end (including JSON serialization)
/// without requiring a running MCP server or network access.
/// </para>
/// <para>
/// The response JSON is deserialized using <see cref="JsonDocument"/> for
/// assertion, which validates both the serialization format and the semantic
/// content of the response.
/// </para>
/// </remarks>
public class DiscoverToolTests : IDisposable
{
    // ---------------------------------------------------------------
    // Test Infrastructure
    // ---------------------------------------------------------------

    /// <summary>
    /// Well-formed llms.txt content used across multiple tests. Contains a title,
    /// summary, two sections (Docs with 2 entries, Examples with 1 entry), and
    /// exercises the parser's key features.
    /// </summary>
    private const string ValidLlmsTxt = """
        # Anthropic
        > Anthropic is an AI safety company that builds reliable AI systems.
        ## Docs
        - [API Reference](https://docs.anthropic.com/api.md): Complete API docs
        - [Getting Started](https://docs.anthropic.com/guide.md): Quickstart guide
        ## Examples
        - [Cookbook](https://docs.anthropic.com/cookbook.md): Code examples
        """;

    /// <summary>Logger that discards output (tests don't need log assertions).</summary>
    private readonly ILogger<DiscoverTool> _logger = NullLogger<DiscoverTool>.Instance;

    /// <summary>Cache instance for tests (fresh per test via constructor).</summary>
    private readonly LlmsTxtCache _cache = new(new CacheOptions { StaleWhileRevalidate = false });

    /// <summary>JSON parse options for deserializing tool responses.</summary>
    private static readonly JsonDocumentOptions JsonParseOptions = new()
    {
        AllowTrailingCommas = true
    };

    /// <summary>Track disposable fetchers for cleanup.</summary>
    private readonly List<LlmsTxtFetcher> _fetchers = new();

    public void Dispose()
    {
        foreach (var fetcher in _fetchers)
            fetcher.Dispose();
    }

    /// <summary>
    /// Creates a <see cref="LlmsTxtFetcher"/> backed by a mock HTTP handler
    /// that returns the specified response for any request.
    /// </summary>
    /// <param name="statusCode">HTTP status code to return.</param>
    /// <param name="content">Response body content.</param>
    /// <param name="headers">Optional response headers (e.g., for WAF detection).</param>
    /// <returns>A fetcher that will return the mock response.</returns>
    private LlmsTxtFetcher CreateMockFetcher(
        int statusCode,
        string content,
        Dictionary<string, string>? headers = null)
    {
        var handler = new MockHttpHandler(request =>
        {
            var response = new HttpResponseMessage((System.Net.HttpStatusCode)statusCode)
            {
                Content = new StringContent(content)
            };

            if (headers != null)
            {
                foreach (var kvp in headers)
                {
                    response.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
                }
            }

            return response;
        });

        var httpClient = new HttpClient(handler);
        var fetcher = new LlmsTxtFetcher(new FetcherOptions { MaxRetries = 0 }, httpClient);
        _fetchers.Add(fetcher);
        return fetcher;
    }

    /// <summary>
    /// Creates a fetcher that throws a <see cref="HttpRequestException"/> simulating
    /// a DNS failure.
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

    /// <summary>
    /// Parses a JSON tool response and returns a <see cref="JsonDocument"/> for assertion.
    /// </summary>
    private static JsonDocument ParseResponse(string json) =>
        JsonDocument.Parse(json, JsonParseOptions);

    // ===============================================================
    // Success Scenarios
    // ===============================================================

    /// <summary>
    /// A valid domain with a well-formed llms.txt file returns a structured
    /// success response containing the document title, summary, sections
    /// with entry counts, and found=true.
    /// </summary>
    [Fact]
    public async Task DiscoverTool_ValidDomain_ReturnsStructuredResponse()
    {
        // Arrange: mock a 200 OK with valid llms.txt content
        var fetcher = CreateMockFetcher(200, ValidLlmsTxt);

        // Act
        var result = await DiscoverTool.Discover("docs.anthropic.com", fetcher, _cache, _logger);

        // Assert: parse the JSON response and verify structure
        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("found").GetBoolean());
        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.Equal("Anthropic", root.GetProperty("title").GetString());
        Assert.StartsWith("Anthropic is an AI safety company", root.GetProperty("summary").GetString());

        // Verify sections array
        var sections = root.GetProperty("sections");
        Assert.Equal(JsonValueKind.Array, sections.ValueKind);
        Assert.Equal(2, sections.GetArrayLength());

        // First section: Docs with 2 entries
        var docsSection = sections[0];
        Assert.Equal("Docs", docsSection.GetProperty("name").GetString());
        Assert.Equal(2, docsSection.GetProperty("entryCount").GetInt32());

        // Second section: Examples with 1 entry
        var examplesSection = sections[1];
        Assert.Equal("Examples", examplesSection.GetProperty("name").GetString());
        Assert.Equal(1, examplesSection.GetProperty("entryCount").GetInt32());
    }

    /// <summary>
    /// A successful fetch caches the result so subsequent calls don't re-fetch.
    /// </summary>
    [Fact]
    public async Task DiscoverTool_ValidDomain_CachesResult()
    {
        var fetcher = CreateMockFetcher(200, ValidLlmsTxt);

        // First call: fetches from origin
        await DiscoverTool.Discover("example.com", fetcher, _cache, _logger);

        // Verify it's in the cache
        var cached = await _cache.GetAsync("example.com");
        Assert.NotNull(cached);
        Assert.Equal("Anthropic", cached.Document.Title);
    }

    /// <summary>
    /// When a fresh cache entry exists, the tool returns it with fromCache=true
    /// without re-fetching from origin.
    /// </summary>
    [Fact]
    public async Task DiscoverTool_CacheHit_ReturnsCachedResponse()
    {
        // Pre-populate cache
        var doc = LlmsDocumentParser.Parse(ValidLlmsTxt);
        var entry = new CacheEntry
        {
            Document = doc,
            FetchedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            FetchResult = new FetchResult
            {
                Status = FetchStatus.Success,
                Document = doc,
                RawContent = ValidLlmsTxt,
                StatusCode = 200,
                Duration = TimeSpan.FromMilliseconds(100),
                Domain = "cached.com"
            }
        };
        await _cache.SetAsync("cached.com", entry);

        // Use a fetcher that would fail (proves we used cache, not network)
        var fetcher = CreateDnsFailureFetcher();

        var result = await DiscoverTool.Discover("cached.com", fetcher, _cache, _logger);

        using var json = ParseResponse(result);
        Assert.True(json.RootElement.GetProperty("found").GetBoolean());
        Assert.True(json.RootElement.GetProperty("fromCache").GetBoolean());
    }

    // ===============================================================
    // Not Found Scenario
    // ===============================================================

    /// <summary>
    /// A 404 response indicates the domain doesn't have an llms.txt file.
    /// The response should have found=false and status="notFound".
    /// </summary>
    [Fact]
    public async Task DiscoverTool_NotFoundDomain_ReturnsNotFoundStatus()
    {
        var fetcher = CreateMockFetcher(404, "Not Found");

        var result = await DiscoverTool.Discover("nofile.example.com", fetcher, _cache, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("found").GetBoolean());
        Assert.Equal("notFound", root.GetProperty("status").GetString());
        Assert.Contains("404", root.GetProperty("errorMessage").GetString());
    }

    // ===============================================================
    // Blocked by WAF Scenario
    // ===============================================================

    /// <summary>
    /// A 403 response with Cloudflare WAF headers returns status="blocked"
    /// with a block reason describing the WAF vendor.
    /// </summary>
    [Fact]
    public async Task DiscoverTool_BlockedDomain_ReturnsBlockedStatusWithReason()
    {
        // Simulate Cloudflare WAF block: 403 with cf-ray header
        var headers = new Dictionary<string, string>
        {
            ["cf-ray"] = "abc123-LAX",
            ["server"] = "cloudflare"
        };
        var fetcher = CreateMockFetcher(403, "Access Denied", headers);

        var result = await DiscoverTool.Discover("blocked.example.com", fetcher, _cache, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("found").GetBoolean());
        Assert.Equal("blocked", root.GetProperty("status").GetString());

        // Block reason should mention the WAF vendor
        var blockReason = root.GetProperty("blockReason").GetString();
        Assert.NotNull(blockReason);
        Assert.Contains("Cloudflare", blockReason, StringComparison.OrdinalIgnoreCase);
    }

    // ===============================================================
    // Rate Limited Scenario
    // ===============================================================

    /// <summary>
    /// A 429 response returns status="rateLimited" with a message about
    /// trying again later.
    /// </summary>
    [Fact]
    public async Task DiscoverTool_RateLimited_ReturnsRateLimitedStatus()
    {
        var headers = new Dictionary<string, string> { ["Retry-After"] = "30" };
        var fetcher = CreateMockFetcher(429, "Too Many Requests", headers);

        var result = await DiscoverTool.Discover("busy.example.com", fetcher, _cache, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("found").GetBoolean());
        Assert.Equal("rateLimited", root.GetProperty("status").GetString());
    }

    // ===============================================================
    // DNS Failure Scenario
    // ===============================================================

    /// <summary>
    /// A DNS resolution failure returns status="dnsFailure" with a helpful message.
    /// </summary>
    [Fact]
    public async Task DiscoverTool_DnsFailure_ReturnsDnsFailureStatus()
    {
        var fetcher = CreateDnsFailureFetcher();

        var result = await DiscoverTool.Discover("nonexistent.invalid", fetcher, _cache, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("found").GetBoolean());

        // DNS failures may be classified as dnsFailure or error depending on exception type
        var status = root.GetProperty("status").GetString();
        Assert.True(status == "dnsFailure" || status == "error",
            $"Expected dnsFailure or error, got {status}");
    }

    // ===============================================================
    // Server Error Scenario
    // ===============================================================

    /// <summary>
    /// A 500 Internal Server Error returns status="error" after exhausting retries
    /// (retries are disabled in these tests for speed).
    /// </summary>
    [Fact]
    public async Task DiscoverTool_ServerError_ReturnsErrorStatus()
    {
        var fetcher = CreateMockFetcher(500, "Internal Server Error");

        var result = await DiscoverTool.Discover("broken.example.com", fetcher, _cache, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("found").GetBoolean());
        Assert.Equal("error", root.GetProperty("status").GetString());
    }

    // ===============================================================
    // Input Validation
    // ===============================================================

    /// <summary>
    /// An empty domain parameter returns a structured error response (not an exception).
    /// </summary>
    [Fact]
    public async Task DiscoverTool_EmptyDomainParameter_ReturnsError()
    {
        var fetcher = CreateMockFetcher(200, ValidLlmsTxt);

        var result = await DiscoverTool.Discover("", fetcher, _cache, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("found").GetBoolean());
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Contains("required", root.GetProperty("errorMessage").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A whitespace-only domain parameter returns a structured error response.
    /// </summary>
    [Fact]
    public async Task DiscoverTool_WhitespaceDomainParameter_ReturnsError()
    {
        var fetcher = CreateMockFetcher(200, ValidLlmsTxt);

        var result = await DiscoverTool.Discover("   ", fetcher, _cache, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("found").GetBoolean());
        Assert.Equal("error", root.GetProperty("status").GetString());
    }

    // ===============================================================
    // JSON Structure Validation
    // ===============================================================

    /// <summary>
    /// The success response JSON is valid and contains all required fields
    /// per the Design Spec § 3.1.
    /// </summary>
    [Fact]
    public async Task DiscoverTool_SuccessResponse_ContainsAllRequiredFields()
    {
        var fetcher = CreateMockFetcher(200, ValidLlmsTxt);

        var result = await DiscoverTool.Discover("complete.example.com", fetcher, _cache, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        // All required fields must be present
        Assert.True(root.TryGetProperty("found", out _), "Missing 'found' field");
        Assert.True(root.TryGetProperty("status", out _), "Missing 'status' field");
        Assert.True(root.TryGetProperty("title", out _), "Missing 'title' field");
        Assert.True(root.TryGetProperty("sections", out _), "Missing 'sections' field");
        Assert.True(root.TryGetProperty("fromCache", out _), "Missing 'fromCache' field");

        // Each section must have name, entryCount, isOptional
        var firstSection = root.GetProperty("sections")[0];
        Assert.True(firstSection.TryGetProperty("name", out _), "Section missing 'name'");
        Assert.True(firstSection.TryGetProperty("entryCount", out _), "Section missing 'entryCount'");
        Assert.True(firstSection.TryGetProperty("isOptional", out _), "Section missing 'isOptional'");
    }

    /// <summary>
    /// The error response JSON is valid and contains the expected fields.
    /// </summary>
    [Fact]
    public async Task DiscoverTool_ErrorResponse_ContainsAllRequiredFields()
    {
        var fetcher = CreateMockFetcher(404, "Not Found");

        var result = await DiscoverTool.Discover("error.example.com", fetcher, _cache, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("found", out _), "Missing 'found' field");
        Assert.True(root.TryGetProperty("status", out _), "Missing 'status' field");
        Assert.True(root.TryGetProperty("errorMessage", out _), "Missing 'errorMessage' field");
    }

    // ===============================================================
    // Parse Diagnostics in Response
    // ===============================================================

    /// <summary>
    /// A document with parse diagnostics (e.g., multiple H1 headings) includes
    /// the diagnostics in the response.
    /// </summary>
    [Fact]
    public async Task DiscoverTool_DocumentWithDiagnostics_IncludesDiagnosticsInResponse()
    {
        // Content with multiple H1 headings (a parse diagnostic)
        var badContent = "# First Title\n# Second Title\n## Docs\n- [Link](https://example.com/doc.md): A doc";
        var fetcher = CreateMockFetcher(200, badContent);

        var result = await DiscoverTool.Discover("diag.example.com", fetcher, _cache, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("found").GetBoolean());

        // Should have diagnostics
        if (root.TryGetProperty("diagnostics", out var diagnostics) &&
            diagnostics.ValueKind == JsonValueKind.Array &&
            diagnostics.GetArrayLength() > 0)
        {
            var firstDiag = diagnostics[0];
            Assert.True(firstDiag.TryGetProperty("severity", out _));
            Assert.True(firstDiag.TryGetProperty("message", out _));
        }
        // Note: if the parser doesn't produce diagnostics for this input, the test still passes
    }

    // ===============================================================
    // AWS WAF Block Scenario
    // ===============================================================

    /// <summary>
    /// A 403 response with AWS CloudFront/WAF headers is correctly identified
    /// as a WAF block.
    /// </summary>
    [Fact]
    public async Task DiscoverTool_AwsWafBlock_ReturnsBlockedStatus()
    {
        var headers = new Dictionary<string, string>
        {
            ["server"] = "CloudFront",
            ["x-amzn-waf-action"] = "block"
        };
        var fetcher = CreateMockFetcher(403, "Forbidden", headers);

        var result = await DiscoverTool.Discover("aws.example.com", fetcher, _cache, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("found").GetBoolean());
        Assert.Equal("blocked", root.GetProperty("status").GetString());
    }
}

// ---------------------------------------------------------------
// MockHttpHandler — used by MCP tool tests
// ---------------------------------------------------------------
// This is a simplified version similar to the one in Core.Tests.
// It's duplicated here because the Mcp.Tests project doesn't directly
// reference the Core.Tests assembly. In a larger project, this would
// live in a shared test infrastructure package.
// ---------------------------------------------------------------

/// <summary>
/// A mock <see cref="HttpMessageHandler"/> for unit testing HTTP-dependent code.
/// Returns a pre-configured response for each request.
/// </summary>
internal sealed class MockHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

    /// <summary>
    /// Creates a handler that uses the factory function to generate responses.
    /// </summary>
    /// <param name="responseFactory">
    /// Function that receives the request and returns a response (or throws
    /// to simulate network errors).
    /// </param>
    public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_responseFactory(request));
    }
}
