using System.Text.Json;
using LlmsTxtKit.Core.Caching;
using LlmsTxtKit.Core.Fetching;
using LlmsTxtKit.Core.Parsing;
using LlmsTxtKit.Core.Validation;
using LlmsTxtKit.Mcp.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlmsTxtKit.Mcp.Tests;

/// <summary>
/// Unit tests for the <c>llmstxt_validate</c> MCP tool (<see cref="ValidateTool"/>).
/// Tests verify the tool's response structure, validation rule execution,
/// error/warning counting, input validation, and error handling.
/// </summary>
/// <remarks>
/// <para>
/// These tests exercise <see cref="ValidateTool.Validate"/> directly. The document
/// is pre-seeded into the cache so tests focus on the validation logic and response
/// formatting rather than HTTP fetching (which is thoroughly tested in DiscoverTool
/// and FetchSectionTool tests).
/// </para>
/// <para>
/// The <see cref="LlmsDocumentValidator"/> is used directly (not mocked) because
/// the built-in structural validation rules are deterministic and fast. Network-dependent
/// rules are disabled (checkUrls=false, checkFreshness=false) to keep tests offline.
/// </para>
/// </remarks>
public class ValidateToolTests : IDisposable
{
    // ---------------------------------------------------------------
    // Test Infrastructure
    // ---------------------------------------------------------------

    /// <summary>
    /// Well-formed llms.txt content that passes all structural validation rules.
    /// </summary>
    private const string ValidLlmsTxt = """
        # TestSite
        > A valid test site with proper structure.
        ## Docs
        - [API Reference](https://example.com/api.md): Complete API docs
        - [Getting Started](https://example.com/guide.md): Quickstart guide
        ## Examples
        - [Cookbook](https://example.com/cookbook.md): Code examples
        """;

    /// <summary>
    /// llms.txt content with a structural error: no H1 heading.
    /// </summary>
    private const string NoH1LlmsTxt = """
        > This document has no title heading.
        ## Docs
        - [Link](https://example.com/doc.md): A doc
        """;

    /// <summary>
    /// llms.txt content with a warning: an empty section.
    /// </summary>
    private const string EmptySectionLlmsTxt = """
        # TestSite
        > Site with an empty section.
        ## Docs
        - [Link](https://example.com/doc.md): A doc
        ## EmptySection
        """;

    /// <summary>
    /// llms.txt content with multiple issues: no H1 and an invalid URL.
    /// </summary>
    private const string MultipleIssuesLlmsTxt = """
        ## Docs
        - [Bad Link](not-a-valid-url): Invalid URL entry
        - [Good Link](https://example.com/ok.md): Valid entry
        """;

    private readonly ILogger<ValidateTool> _logger = NullLogger<ValidateTool>.Instance;
    private readonly LlmsTxtCache _cache = new(new CacheOptions { StaleWhileRevalidate = false });
    private readonly LlmsDocumentValidator _validator = new();
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

    // ===============================================================
    // Valid Document Scenarios
    // ===============================================================

    /// <summary>
    /// A well-formed document validates successfully with isValid=true,
    /// zero errors, and zero warnings.
    /// </summary>
    [Fact]
    public async Task ValidateTool_ValidDocument_ReturnsIsValidTrue()
    {
        await SeedCacheAsync("valid.com", ValidLlmsTxt);

        var result = await ValidateTool.Validate(
            "valid.com", false, false,
            CreateDnsFailureFetcher(), _cache, _validator, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("isValid").GetBoolean());
        Assert.Equal(0, root.GetProperty("errorCount").GetInt32());
        Assert.Equal(0, root.GetProperty("warningCount").GetInt32());
        Assert.Equal("valid.com", root.GetProperty("domain").GetString());
        Assert.Equal("TestSite", root.GetProperty("title").GetString());
    }

    /// <summary>
    /// A valid document returns an empty issues array, not null.
    /// </summary>
    [Fact]
    public async Task ValidateTool_ValidDocument_ReturnsEmptyIssuesArray()
    {
        await SeedCacheAsync("valid.com", ValidLlmsTxt);

        var result = await ValidateTool.Validate(
            "valid.com", false, false,
            CreateDnsFailureFetcher(), _cache, _validator, _logger);

        using var doc = ParseResponse(result);
        var issues = doc.RootElement.GetProperty("issues");
        Assert.Equal(JsonValueKind.Array, issues.ValueKind);
        Assert.Equal(0, issues.GetArrayLength());
    }

    // ===============================================================
    // Invalid Document Scenarios
    // ===============================================================

    /// <summary>
    /// A document without an H1 heading produces at least one error-severity issue
    /// and isValid=false.
    /// </summary>
    [Fact]
    public async Task ValidateTool_NoH1Document_ReturnsIsValidFalse()
    {
        await SeedCacheAsync("invalid.com", NoH1LlmsTxt);

        var result = await ValidateTool.Validate(
            "invalid.com", false, false,
            CreateDnsFailureFetcher(), _cache, _validator, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("isValid").GetBoolean());
        Assert.True(root.GetProperty("errorCount").GetInt32() > 0);
    }

    /// <summary>
    /// A document with an empty section produces a warning-severity issue.
    /// The document is still valid (warnings don't invalidate).
    /// </summary>
    [Fact]
    public async Task ValidateTool_EmptySection_ReturnsWarning()
    {
        await SeedCacheAsync("warn.com", EmptySectionLlmsTxt);

        var result = await ValidateTool.Validate(
            "warn.com", false, false,
            CreateDnsFailureFetcher(), _cache, _validator, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        // Should be valid (warnings don't invalidate)
        Assert.True(root.GetProperty("isValid").GetBoolean());
        Assert.True(root.GetProperty("warningCount").GetInt32() > 0);
    }

    /// <summary>
    /// A document with multiple issues returns all of them in the issues array.
    /// Issues are sorted by severity (errors first).
    /// </summary>
    [Fact]
    public async Task ValidateTool_MultipleIssues_ReturnsAll()
    {
        await SeedCacheAsync("multi.com", MultipleIssuesLlmsTxt);

        var result = await ValidateTool.Validate(
            "multi.com", false, false,
            CreateDnsFailureFetcher(), _cache, _validator, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        var issues = root.GetProperty("issues");
        Assert.True(issues.GetArrayLength() > 0,
            "Expected multiple validation issues but got none");
    }

    // ===============================================================
    // Issue Structure Validation
    // ===============================================================

    /// <summary>
    /// Each issue in the response has the expected fields: severity, rule,
    /// message, and optionally location.
    /// </summary>
    [Fact]
    public async Task ValidateTool_IssueStructure_ContainsRequiredFields()
    {
        await SeedCacheAsync("issue.com", NoH1LlmsTxt);

        var result = await ValidateTool.Validate(
            "issue.com", false, false,
            CreateDnsFailureFetcher(), _cache, _validator, _logger);

        using var doc = ParseResponse(result);
        var issues = doc.RootElement.GetProperty("issues");
        Assert.True(issues.GetArrayLength() > 0);

        var firstIssue = issues[0];
        Assert.True(firstIssue.TryGetProperty("severity", out _), "Issue missing 'severity'");
        Assert.True(firstIssue.TryGetProperty("rule", out _), "Issue missing 'rule'");
        Assert.True(firstIssue.TryGetProperty("message", out _), "Issue missing 'message'");

        // Severity should be "error" or "warning"
        var severity = firstIssue.GetProperty("severity").GetString();
        Assert.True(severity == "error" || severity == "warning",
            $"Unexpected severity: {severity}");
    }

    /// <summary>
    /// Error-severity issues contain a machine-readable rule ID like
    /// "REQUIRED_H1_MISSING" (stable across versions for programmatic handling).
    /// </summary>
    [Fact]
    public async Task ValidateTool_ErrorIssue_HasRuleId()
    {
        await SeedCacheAsync("ruleid.com", NoH1LlmsTxt);

        var result = await ValidateTool.Validate(
            "ruleid.com", false, false,
            CreateDnsFailureFetcher(), _cache, _validator, _logger);

        using var doc = ParseResponse(result);
        var issues = doc.RootElement.GetProperty("issues");

        var errorIssues = Enumerable.Range(0, issues.GetArrayLength())
            .Select(i => issues[i])
            .Where(i => i.GetProperty("severity").GetString() == "error")
            .ToList();

        Assert.NotEmpty(errorIssues);

        // Rule ID should be a non-empty string
        var rule = errorIssues[0].GetProperty("rule").GetString();
        Assert.False(string.IsNullOrWhiteSpace(rule), "Rule ID should not be empty");
    }

    // ===============================================================
    // Fetch from Origin (No Cache)
    // ===============================================================

    /// <summary>
    /// When the document is not cached, the tool fetches it from origin.
    /// </summary>
    [Fact]
    public async Task ValidateTool_NoCacheHit_FetchesFromOrigin()
    {
        var fetcher = CreateMockFetcher(200, ValidLlmsTxt);

        var result = await ValidateTool.Validate(
            "fresh.com", false, false,
            fetcher, _cache, _validator, _logger);

        using var doc = ParseResponse(result);
        Assert.True(doc.RootElement.GetProperty("isValid").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("fromCache").GetBoolean());
    }

    // ===============================================================
    // Domain Fetch Errors
    // ===============================================================

    /// <summary>
    /// A 404 response returns a fetch-level error (cannot validate what doesn't exist).
    /// </summary>
    [Fact]
    public async Task ValidateTool_DomainNotFound_ReturnsError()
    {
        var fetcher = CreateMockFetcher(404, "Not Found");

        var result = await ValidateTool.Validate(
            "nofile.com", false, false,
            fetcher, _cache, _validator, _logger);

        using var doc = ParseResponse(result);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("404", doc.RootElement.GetProperty("errorMessage").GetString());
    }

    // ===============================================================
    // Input Validation
    // ===============================================================

    /// <summary>
    /// An empty domain parameter returns a structured error response.
    /// </summary>
    [Fact]
    public async Task ValidateTool_EmptyDomain_ReturnsError()
    {
        var result = await ValidateTool.Validate(
            "", false, false,
            CreateMockFetcher(200, ValidLlmsTxt), _cache, _validator, _logger);

        using var doc = ParseResponse(result);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("domain", doc.RootElement.GetProperty("errorMessage").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A whitespace-only domain parameter returns a structured error response.
    /// </summary>
    [Fact]
    public async Task ValidateTool_WhitespaceDomain_ReturnsError()
    {
        var result = await ValidateTool.Validate(
            "   ", false, false,
            CreateMockFetcher(200, ValidLlmsTxt), _cache, _validator, _logger);

        using var doc = ParseResponse(result);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
    }

    // ===============================================================
    // Response Structure Validation
    // ===============================================================

    /// <summary>
    /// The success response contains all required fields per Design Spec § 3.3.
    /// </summary>
    [Fact]
    public async Task ValidateTool_SuccessResponse_ContainsAllRequiredFields()
    {
        await SeedCacheAsync("complete.com", ValidLlmsTxt);

        var result = await ValidateTool.Validate(
            "complete.com", false, false,
            CreateDnsFailureFetcher(), _cache, _validator, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("isValid", out _), "Missing 'isValid' field");
        Assert.True(root.TryGetProperty("errorCount", out _), "Missing 'errorCount' field");
        Assert.True(root.TryGetProperty("warningCount", out _), "Missing 'warningCount' field");
        Assert.True(root.TryGetProperty("issues", out _), "Missing 'issues' field");
        Assert.True(root.TryGetProperty("domain", out _), "Missing 'domain' field");
        Assert.True(root.TryGetProperty("title", out _), "Missing 'title' field");
        Assert.True(root.TryGetProperty("fromCache", out _), "Missing 'fromCache' field");
    }

    /// <summary>
    /// The error response (when domain can't be fetched) contains all expected fields.
    /// </summary>
    [Fact]
    public async Task ValidateTool_ErrorResponse_ContainsAllRequiredFields()
    {
        var fetcher = CreateMockFetcher(404, "Not Found");

        var result = await ValidateTool.Validate(
            "missing.com", false, false,
            fetcher, _cache, _validator, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("success", out _), "Missing 'success' field");
        Assert.True(root.TryGetProperty("errorMessage", out _), "Missing 'errorMessage' field");
    }

    // ===============================================================
    // Cache Hit Behavior
    // ===============================================================

    /// <summary>
    /// When the document is in cache, fromCache=true in the response.
    /// </summary>
    [Fact]
    public async Task ValidateTool_CacheHit_SetsFromCacheTrue()
    {
        await SeedCacheAsync("cached.com", ValidLlmsTxt);

        var result = await ValidateTool.Validate(
            "cached.com", false, false,
            CreateDnsFailureFetcher(), _cache, _validator, _logger);

        using var doc = ParseResponse(result);
        Assert.True(doc.RootElement.GetProperty("fromCache").GetBoolean());
    }
}
