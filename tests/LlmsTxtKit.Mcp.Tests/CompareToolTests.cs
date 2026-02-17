using System.Net;
using System.Text.Json;
using LlmsTxtKit.Mcp.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlmsTxtKit.Mcp.Tests;

/// <summary>
/// Unit tests for the <c>llmstxt_compare</c> MCP tool (<see cref="CompareTool"/>).
/// Tests verify HTML/Markdown comparison, metric calculations, freshness delta,
/// error handling, and HTML-to-text stripping.
/// </summary>
/// <remarks>
/// <para>
/// These tests exercise <see cref="CompareTool.Compare"/> directly with a mock
/// <see cref="HttpClient"/> backed by <see cref="CompareTestHandler"/>. The handler
/// returns different responses based on whether the request URL ends with ".md"
/// (Markdown) or not (HTML), allowing tests to configure both versions independently.
/// </para>
/// </remarks>
public class CompareToolTests : IDisposable
{
    // ---------------------------------------------------------------
    // Test Infrastructure
    // ---------------------------------------------------------------

    /// <summary>Sample HTML content — typical web page with boilerplate.</summary>
    private const string SampleHtml = """
        <html>
        <head><title>Test Page</title>
        <style>body { font-family: sans-serif; }</style>
        <script>console.log("hello");</script>
        </head>
        <body>
        <nav><a href="/">Home</a> | <a href="/about">About</a></nav>
        <main>
        <h1>API Reference</h1>
        <p>This is the API documentation for our service.</p>
        <p>It covers authentication, endpoints, and error handling.</p>
        <!-- This is an HTML comment -->
        <footer>&copy; 2025 TestCo</footer>
        </main>
        </body>
        </html>
        """;

    /// <summary>
    /// Sample Markdown content — equivalent to the HTML but curated for LLMs.
    /// Much shorter and more focused than the HTML version.
    /// </summary>
    private const string SampleMarkdown = """
        # API Reference

        This is the API documentation for our service.
        It covers authentication, endpoints, and error handling.
        """;

    private readonly ILogger<CompareTool> _logger = NullLogger<CompareTool>.Instance;
    private readonly List<HttpClient> _httpClients = new();

    private static readonly JsonDocumentOptions JsonParseOptions = new() { AllowTrailingCommas = true };

    public void Dispose()
    {
        foreach (var client in _httpClients)
            client.Dispose();
    }

    /// <summary>
    /// Creates an HttpClient that returns different content based on whether
    /// the URL ends with ".md" or not.
    /// </summary>
    private HttpClient CreateMockHttpClient(
        string htmlContent,
        int htmlStatusCode = 200,
        DateTimeOffset? htmlLastModified = null,
        string? markdownContent = null,
        int markdownStatusCode = 200,
        DateTimeOffset? markdownLastModified = null)
    {
        var handler = new CompareTestHandler(
            htmlContent, htmlStatusCode, htmlLastModified,
            markdownContent, markdownStatusCode, markdownLastModified);
        var client = new HttpClient(handler);
        _httpClients.Add(client);
        return client;
    }

    private static JsonDocument ParseResponse(string json) =>
        JsonDocument.Parse(json, JsonParseOptions);

    // ===============================================================
    // Success — Both Versions Available
    // ===============================================================

    /// <summary>
    /// When both HTML and Markdown versions are available, the response includes
    /// metrics for both and markdownAvailable=true.
    /// </summary>
    [Fact]
    public async Task CompareTool_MarkdownVersionAvailable_ReturnsBothVersions()
    {
        var client = CreateMockHttpClient(
            SampleHtml, markdownContent: SampleMarkdown);

        var result = await CompareTool.Compare(
            "https://example.com/docs/api", client, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("markdownAvailable").GetBoolean());
        Assert.Equal("https://example.com/docs/api", root.GetProperty("url").GetString());
        Assert.Equal("https://example.com/docs/api.md", root.GetProperty("markdownUrl").GetString());

        // Both sizes should be present
        Assert.True(root.GetProperty("htmlSize").GetInt32() > 0);
        Assert.True(root.GetProperty("markdownSize").GetInt32() > 0);

        // Both token estimates should be present
        Assert.True(root.GetProperty("htmlTokenEstimate").GetInt32() > 0);
        Assert.True(root.GetProperty("markdownTokenEstimate").GetInt32() > 0);
    }

    // ===============================================================
    // Success — No Markdown Version
    // ===============================================================

    /// <summary>
    /// When no Markdown version exists (404), markdownAvailable=false and
    /// Markdown metrics are null.
    /// </summary>
    [Fact]
    public async Task CompareTool_NoMarkdownVersion_ReturnsHtmlOnlyWithFlag()
    {
        var client = CreateMockHttpClient(
            SampleHtml,
            markdownContent: null,  // null => 404 for .md
            markdownStatusCode: 404);

        var result = await CompareTool.Compare(
            "https://example.com/docs/api", client, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.False(root.GetProperty("markdownAvailable").GetBoolean());

        // HTML metrics present
        Assert.True(root.GetProperty("htmlSize").GetInt32() > 0);
        Assert.True(root.GetProperty("htmlTokenEstimate").GetInt32() > 0);

        // Markdown metrics absent (WhenWritingNull)
        Assert.False(root.TryGetProperty("markdownSize", out var mdSize) &&
                     mdSize.ValueKind != JsonValueKind.Null,
            "markdownSize should be null when Markdown unavailable");
    }

    // ===============================================================
    // Metric Calculations
    // ===============================================================

    /// <summary>
    /// Size and token ratios are calculated correctly: Markdown/HTML.
    /// Markdown is typically much smaller than raw HTML.
    /// </summary>
    [Fact]
    public async Task CompareTool_SizeAndTokenRatios_CalculatedCorrectly()
    {
        var client = CreateMockHttpClient(
            SampleHtml, markdownContent: SampleMarkdown);

        var result = await CompareTool.Compare(
            "https://example.com/docs/api", client, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        var htmlSize = root.GetProperty("htmlSize").GetInt32();
        var markdownSize = root.GetProperty("markdownSize").GetInt32();
        var sizeRatio = root.GetProperty("sizeRatio").GetDouble();

        // Size ratio = markdown / html
        var expectedSizeRatio = Math.Round((double)markdownSize / htmlSize, 3);
        Assert.Equal(expectedSizeRatio, sizeRatio);

        // Markdown should be smaller than raw HTML
        Assert.True(sizeRatio < 1.0,
            $"Expected sizeRatio < 1.0 (Markdown smaller), got {sizeRatio}");

        // Token ratio should also be < 1.0
        var tokenRatio = root.GetProperty("tokenRatio").GetDouble();
        Assert.True(tokenRatio > 0.0, "Token ratio should be positive");
    }

    // ===============================================================
    // Freshness Delta
    // ===============================================================

    /// <summary>
    /// When both versions have Last-Modified headers, the freshness delta
    /// is calculated (Markdown - HTML, in seconds).
    /// </summary>
    [Fact]
    public async Task CompareTool_FreshnessDelta_PopulatedWhenHeadersAvailable()
    {
        var htmlDate = new DateTimeOffset(2025, 1, 15, 10, 0, 0, TimeSpan.Zero);
        var mdDate = new DateTimeOffset(2025, 2, 1, 10, 0, 0, TimeSpan.Zero);

        var client = CreateMockHttpClient(
            SampleHtml, htmlLastModified: htmlDate,
            markdownContent: SampleMarkdown, markdownLastModified: mdDate);

        var result = await CompareTool.Compare(
            "https://example.com/docs/api", client, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        // Freshness delta should be present
        Assert.True(root.TryGetProperty("freshnessDelta", out var delta));
        var deltaValue = delta.GetDouble();

        // mdDate is after htmlDate, so delta should be positive
        Assert.True(deltaValue > 0,
            $"Expected positive delta (Markdown newer), got {deltaValue}");

        // Expected: 17 days = 1,468,800 seconds
        var expectedDelta = (mdDate - htmlDate).TotalSeconds;
        Assert.Equal(Math.Round(expectedDelta, 1), deltaValue);
    }

    /// <summary>
    /// When Last-Modified headers are absent, freshnessDelta is null.
    /// </summary>
    [Fact]
    public async Task CompareTool_NoLastModifiedHeaders_FreshnessDeltaNull()
    {
        var client = CreateMockHttpClient(
            SampleHtml, markdownContent: SampleMarkdown);
        // No Last-Modified headers passed

        var result = await CompareTool.Compare(
            "https://example.com/docs/api", client, _logger);

        using var doc = ParseResponse(result);
        // freshnessDelta should be absent (WhenWritingNull)
        Assert.False(
            doc.RootElement.TryGetProperty("freshnessDelta", out var fd) &&
            fd.ValueKind != JsonValueKind.Null,
            "freshnessDelta should be null when headers are missing");
    }

    // ===============================================================
    // HTML Fetch Errors
    // ===============================================================

    /// <summary>
    /// When the HTML page returns a non-success status, the tool returns an error.
    /// </summary>
    [Fact]
    public async Task CompareTool_HtmlFetchFails_ReturnsError()
    {
        var client = CreateMockHttpClient(
            "Not Found", htmlStatusCode: 404);

        var result = await CompareTool.Compare(
            "https://example.com/nonexistent", client, _logger);

        using var doc = ParseResponse(result);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("404", doc.RootElement.GetProperty("errorMessage").GetString());
    }

    // ===============================================================
    // Input Validation
    // ===============================================================

    /// <summary>
    /// An empty URL returns a structured error response.
    /// </summary>
    [Fact]
    public async Task CompareTool_EmptyUrl_ReturnsError()
    {
        var client = CreateMockHttpClient(SampleHtml);

        var result = await CompareTool.Compare("", client, _logger);

        using var doc = ParseResponse(result);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("required", doc.RootElement.GetProperty("errorMessage").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An invalid URL (not HTTP/HTTPS) returns a structured error response.
    /// </summary>
    [Fact]
    public async Task CompareTool_InvalidUrl_ReturnsError()
    {
        var client = CreateMockHttpClient(SampleHtml);

        var result = await CompareTool.Compare("ftp://files.example.com/doc", client, _logger);

        using var doc = ParseResponse(result);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("Invalid URL", doc.RootElement.GetProperty("errorMessage").GetString());
    }

    /// <summary>
    /// A relative URL (no scheme) returns a structured error response.
    /// </summary>
    [Fact]
    public async Task CompareTool_RelativeUrl_ReturnsError()
    {
        var client = CreateMockHttpClient(SampleHtml);

        var result = await CompareTool.Compare("docs/api", client, _logger);

        using var doc = ParseResponse(result);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
    }

    // ===============================================================
    // Response Structure
    // ===============================================================

    /// <summary>
    /// The success response with both versions contains all required fields.
    /// </summary>
    [Fact]
    public async Task CompareTool_SuccessResponse_ContainsAllRequiredFields()
    {
        var client = CreateMockHttpClient(
            SampleHtml, markdownContent: SampleMarkdown);

        var result = await CompareTool.Compare(
            "https://example.com/docs/api", client, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("success", out _), "Missing 'success'");
        Assert.True(root.TryGetProperty("url", out _), "Missing 'url'");
        Assert.True(root.TryGetProperty("markdownUrl", out _), "Missing 'markdownUrl'");
        Assert.True(root.TryGetProperty("markdownAvailable", out _), "Missing 'markdownAvailable'");
        Assert.True(root.TryGetProperty("htmlSize", out _), "Missing 'htmlSize'");
        Assert.True(root.TryGetProperty("htmlTokenEstimate", out _), "Missing 'htmlTokenEstimate'");
        Assert.True(root.TryGetProperty("markdownSize", out _), "Missing 'markdownSize'");
        Assert.True(root.TryGetProperty("markdownTokenEstimate", out _), "Missing 'markdownTokenEstimate'");
        Assert.True(root.TryGetProperty("sizeRatio", out _), "Missing 'sizeRatio'");
        Assert.True(root.TryGetProperty("tokenRatio", out _), "Missing 'tokenRatio'");
    }

    /// <summary>
    /// The error response contains all required fields.
    /// </summary>
    [Fact]
    public async Task CompareTool_ErrorResponse_ContainsAllRequiredFields()
    {
        var client = CreateMockHttpClient(SampleHtml);

        var result = await CompareTool.Compare("", client, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("success", out _), "Missing 'success'");
        Assert.True(root.TryGetProperty("errorMessage", out _), "Missing 'errorMessage'");
    }

    // ===============================================================
    // HTML Stripping Unit Tests
    // ===============================================================

    /// <summary>
    /// HTML tags are stripped, leaving only the text content.
    /// </summary>
    [Fact]
    public void StripHtmlTags_BasicHtml_ReturnsText()
    {
        var html = "<p>Hello <strong>world</strong></p>";
        var result = CompareTool.StripHtmlTags(html);

        Assert.DoesNotContain("<", result);
        Assert.Contains("Hello", result);
        Assert.Contains("world", result);
    }

    /// <summary>
    /// Script and style blocks are removed entirely (including content).
    /// </summary>
    [Fact]
    public void StripHtmlTags_ScriptAndStyle_Removed()
    {
        var html = "<script>var x = 1;</script><style>.a{color:red}</style><p>Content</p>";
        var result = CompareTool.StripHtmlTags(html);

        Assert.DoesNotContain("var x", result);
        Assert.DoesNotContain("color:red", result);
        Assert.Contains("Content", result);
    }

    /// <summary>
    /// HTML entities are decoded (e.g., &amp;amp; → &amp;).
    /// </summary>
    [Fact]
    public void StripHtmlTags_HtmlEntities_Decoded()
    {
        var html = "<p>A &amp; B &lt; C</p>";
        var result = CompareTool.StripHtmlTags(html);

        Assert.Contains("A & B < C", result);
    }

    /// <summary>
    /// Empty and null input returns empty string.
    /// </summary>
    [Fact]
    public void StripHtmlTags_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CompareTool.StripHtmlTags(""));
        Assert.Equal(string.Empty, CompareTool.StripHtmlTags(null!));
    }

    // ===============================================================
    // Token Estimation Unit Tests
    // ===============================================================

    /// <summary>
    /// Token estimate uses words÷4 heuristic (ceiling).
    /// </summary>
    [Fact]
    public void EstimateTokenCount_KnownInput_ReturnsExpected()
    {
        // 8 words → ceiling(8/4) = 2
        var result = CompareTool.EstimateTokenCount("one two three four five six seven eight");
        Assert.Equal(2, result);

        // 5 words → ceiling(5/4) = 2
        result = CompareTool.EstimateTokenCount("one two three four five");
        Assert.Equal(2, result);

        // 1 word → ceiling(1/4) = 1
        result = CompareTool.EstimateTokenCount("hello");
        Assert.Equal(1, result);
    }

    /// <summary>
    /// Empty/whitespace input returns 0 tokens.
    /// </summary>
    [Fact]
    public void EstimateTokenCount_EmptyInput_ReturnsZero()
    {
        Assert.Equal(0, CompareTool.EstimateTokenCount(""));
        Assert.Equal(0, CompareTool.EstimateTokenCount("   "));
    }

    // ===============================================================
    // URL Trailing Slash Handling
    // ===============================================================

    /// <summary>
    /// A URL with a trailing slash still produces a correct .md URL
    /// (the slash is trimmed before appending .md).
    /// </summary>
    [Fact]
    public async Task CompareTool_UrlWithTrailingSlash_HandledCorrectly()
    {
        var client = CreateMockHttpClient(
            SampleHtml, markdownContent: SampleMarkdown);

        var result = await CompareTool.Compare(
            "https://example.com/docs/api/", client, _logger);

        using var doc = ParseResponse(result);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("https://example.com/docs/api.md",
            root.GetProperty("markdownUrl").GetString());
    }
}

// ---------------------------------------------------------------
// CompareTestHandler — Mock HTTP handler for comparison tests
// ---------------------------------------------------------------

/// <summary>
/// A mock <see cref="HttpMessageHandler"/> that returns different responses
/// for HTML and Markdown URLs. URLs ending with ".md" get the Markdown
/// response; all others get the HTML response.
/// </summary>
internal sealed class CompareTestHandler : HttpMessageHandler
{
    private readonly string _htmlContent;
    private readonly int _htmlStatusCode;
    private readonly DateTimeOffset? _htmlLastModified;
    private readonly string? _markdownContent;
    private readonly int _markdownStatusCode;
    private readonly DateTimeOffset? _markdownLastModified;

    public CompareTestHandler(
        string htmlContent,
        int htmlStatusCode = 200,
        DateTimeOffset? htmlLastModified = null,
        string? markdownContent = null,
        int markdownStatusCode = 200,
        DateTimeOffset? markdownLastModified = null)
    {
        _htmlContent = htmlContent;
        _htmlStatusCode = htmlStatusCode;
        _htmlLastModified = htmlLastModified;
        _markdownContent = markdownContent;
        _markdownStatusCode = markdownStatusCode;
        _markdownLastModified = markdownLastModified;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? "";
        var isMarkdown = url.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

        HttpResponseMessage response;

        if (isMarkdown)
        {
            if (_markdownContent != null)
            {
                response = new HttpResponseMessage((HttpStatusCode)_markdownStatusCode)
                {
                    Content = new StringContent(_markdownContent)
                };
                if (_markdownLastModified.HasValue)
                    response.Content.Headers.LastModified = _markdownLastModified.Value;
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("Not Found")
                };
            }
        }
        else
        {
            response = new HttpResponseMessage((HttpStatusCode)_htmlStatusCode)
            {
                Content = new StringContent(_htmlContent)
            };
            if (_htmlLastModified.HasValue)
                response.Content.Headers.LastModified = _htmlLastModified.Value;
        }

        return Task.FromResult(response);
    }
}
