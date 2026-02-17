using System.ComponentModel;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace LlmsTxtKit.Mcp.Tools;

/// <summary>
/// MCP tool: <c>llmstxt_compare</c>. Given a URL, fetches both the HTML page and
/// the llms.txt-linked Markdown version (if available at <c>{url}.md</c>) and
/// reports comparative metrics: size ratios, token ratios, and freshness delta.
/// </summary>
/// <remarks>
/// <para>
/// This tool is architecturally distinct from the other four MCP tools. While they
/// operate on the llms.txt file itself (discover, fetch sections, validate, generate
/// context), this tool compares a <em>specific page</em> in both its HTML and
/// Markdown forms. The purpose is to quantify the benefit of llms.txt-curated
/// Markdown content over raw HTML retrieval — a key metric for the Context Collapse
/// Mitigation Benchmark study (see PRS § 6 and Design Spec § 3.5).
/// </para>
/// <para>
/// The HTML-to-text conversion is intentionally simple: strip tags and normalize
/// whitespace. This simulates how most retrieval pipelines pre-process HTML before
/// injecting it into an LLM context window, making the comparison representative
/// of real-world token usage differences.
/// </para>
/// <para>
/// <b>Design Spec Reference:</b> § 3.5 (llmstxt_compare tool definition).
/// </para>
/// <para>
/// <b>User Story:</b> "As an AI agent, I want to compare HTML and Markdown versions
/// of the same page so that I can measure the benefit of llms.txt-curated content
/// over standard web retrieval."
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class CompareTool
{
    // ---------------------------------------------------------------
    // JSON Serialization Options
    // ---------------------------------------------------------------

    /// <summary>
    /// Shared JSON serialization options for tool responses. Uses camelCase
    /// property naming to align with MCP convention.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // ---------------------------------------------------------------
    // HTML Tag Stripping Regex
    // ---------------------------------------------------------------

    /// <summary>
    /// Regex to strip HTML tags from fetched HTML content. Matches any
    /// opening/closing tag including self-closing tags and tags with attributes.
    /// This is intentionally simple — a full HTML parser would be overkill here
    /// since we're simulating the rough text extraction that retrieval pipelines
    /// perform before injecting HTML into an LLM context.
    /// </summary>
    private static readonly Regex HtmlTagPattern = new(
        @"<[^>]+>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Regex to strip HTML/XML comments.
    /// </summary>
    private static readonly Regex HtmlCommentPattern = new(
        @"<!--[\s\S]*?-->",
        RegexOptions.Compiled);

    /// <summary>
    /// Regex to strip &lt;script&gt; blocks (including content).
    /// </summary>
    private static readonly Regex ScriptPattern = new(
        @"<script[\s\S]*?</script>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to strip &lt;style&gt; blocks (including content).
    /// </summary>
    private static readonly Regex StylePattern = new(
        @"<style[\s\S]*?</style>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Regex to normalize consecutive whitespace (multiple spaces, tabs,
    /// newlines) into single spaces for cleaner text extraction.
    /// </summary>
    private static readonly Regex WhitespaceNormalizationPattern = new(
        @"\s+",
        RegexOptions.Compiled);

    // ---------------------------------------------------------------
    // Tool Method
    // ---------------------------------------------------------------

    /// <summary>
    /// Compares the HTML and Markdown versions of a given URL. Fetches the
    /// HTML page at the URL, then checks for a Markdown version at <c>{url}.md</c>.
    /// Returns comparative metrics including size ratios, token ratios, and
    /// freshness deltas.
    /// </summary>
    /// <param name="url">
    /// The specific page URL to compare (e.g., "https://docs.anthropic.com/api").
    /// The tool fetches this URL for the HTML version, then checks <c>{url}.md</c>
    /// for the Markdown version.
    /// </param>
    /// <param name="httpClient">
    /// The <see cref="HttpClient"/> service, injected by the MCP server's DI container.
    /// Used to fetch both the HTML and Markdown versions of the page.
    /// </param>
    /// <param name="logger">
    /// Logger for diagnostic output (stderr per MCP convention).
    /// </param>
    /// <returns>
    /// A JSON string containing the comparison result:
    /// <list type="bullet">
    /// <item><b>Both available:</b> <c>{ success: true, markdownAvailable: true, htmlSize, markdownSize, sizeRatio, htmlTokenEstimate, markdownTokenEstimate, tokenRatio, freshnessDelta? }</c></item>
    /// <item><b>HTML only:</b> <c>{ success: true, markdownAvailable: false, htmlSize, htmlTokenEstimate, ... }</c></item>
    /// <item><b>Error:</b> <c>{ success: false, errorMessage: "..." }</c></item>
    /// </list>
    /// </returns>
    [McpServerTool(Name = "llmstxt_compare")]
    [Description(
        "Given a URL, fetches both the HTML page and the llms.txt-linked Markdown version " +
        "(at {url}.md) and reports comparative metrics. Use this to measure the benefit of " +
        "llms.txt-curated content over standard HTML retrieval. Returns size ratios, token " +
        "ratios, and freshness deltas between the two versions. If no Markdown version is " +
        "available, returns HTML-only metrics with markdown_available: false.")]
    public static async Task<string> Compare(
        [Description("The specific page URL to compare (e.g., 'https://docs.anthropic.com/api'). " +
                     "The tool fetches this URL for HTML and checks {url}.md for Markdown.")]
        string url,
        HttpClient httpClient,
        ILogger<CompareTool> logger)
    {
        // ---------------------------------------------------------------
        // Input validation
        // ---------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(url))
        {
            logger.LogWarning("llmstxt_compare called with empty url parameter");
            return SerializeResponse(new CompareErrorResponse
            {
                Success = false,
                ErrorMessage = "The 'url' parameter is required and cannot be empty."
            });
        }

        // Validate URL format
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != "http" && parsedUrl.Scheme != "https"))
        {
            logger.LogWarning("llmstxt_compare called with invalid URL: '{Url}'", url);
            return SerializeResponse(new CompareErrorResponse
            {
                Success = false,
                ErrorMessage = $"Invalid URL: '{url}'. Must be an absolute HTTP or HTTPS URL."
            });
        }

        logger.LogInformation("llmstxt_compare: Comparing HTML and Markdown for '{Url}'", url);

        // ---------------------------------------------------------------
        // Construct the Markdown URL ({url}.md)
        // ---------------------------------------------------------------
        var markdownUrl = new Uri(url.TrimEnd('/') + ".md");
        logger.LogDebug("llmstxt_compare: Markdown URL will be '{MarkdownUrl}'", markdownUrl);

        // ---------------------------------------------------------------
        // Fetch HTML version
        // ---------------------------------------------------------------
        string? htmlContent = null;
        DateTimeOffset? htmlLastModified = null;
        int? htmlStatusCode = null;

        try
        {
            logger.LogDebug("llmstxt_compare: Fetching HTML from '{Url}'", parsedUrl);
            var htmlResponse = await httpClient.GetAsync(parsedUrl);
            htmlStatusCode = (int)htmlResponse.StatusCode;

            if (!htmlResponse.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "llmstxt_compare: HTML fetch returned {StatusCode} for '{Url}'",
                    htmlStatusCode, parsedUrl);
                return SerializeResponse(new CompareErrorResponse
                {
                    Success = false,
                    ErrorMessage = $"HTML fetch failed with HTTP {htmlStatusCode} for '{url}'."
                });
            }

            htmlContent = await htmlResponse.Content.ReadAsStringAsync();

            // Extract Last-Modified header if present
            if (htmlResponse.Content.Headers.LastModified.HasValue)
            {
                htmlLastModified = htmlResponse.Content.Headers.LastModified.Value;
                logger.LogDebug(
                    "llmstxt_compare: HTML Last-Modified: {LastModified}",
                    htmlLastModified);
            }

            logger.LogDebug(
                "llmstxt_compare: HTML fetched — {Size} bytes",
                htmlContent.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "llmstxt_compare: Exception fetching HTML from '{Url}'", parsedUrl);
            return SerializeResponse(new CompareErrorResponse
            {
                Success = false,
                ErrorMessage = $"Error fetching HTML: {ex.Message}"
            });
        }

        // ---------------------------------------------------------------
        // Fetch Markdown version
        // ---------------------------------------------------------------
        string? markdownContent = null;
        DateTimeOffset? markdownLastModified = null;
        bool markdownAvailable = false;

        try
        {
            logger.LogDebug("llmstxt_compare: Fetching Markdown from '{MarkdownUrl}'", markdownUrl);
            var mdResponse = await httpClient.GetAsync(markdownUrl);

            if (mdResponse.IsSuccessStatusCode)
            {
                markdownContent = await mdResponse.Content.ReadAsStringAsync();
                markdownAvailable = true;

                if (mdResponse.Content.Headers.LastModified.HasValue)
                {
                    markdownLastModified = mdResponse.Content.Headers.LastModified.Value;
                    logger.LogDebug(
                        "llmstxt_compare: Markdown Last-Modified: {LastModified}",
                        markdownLastModified);
                }

                logger.LogInformation(
                    "llmstxt_compare: Markdown version available — {Size} bytes",
                    markdownContent.Length);
            }
            else
            {
                logger.LogInformation(
                    "llmstxt_compare: No Markdown version found (HTTP {StatusCode} for '{MarkdownUrl}')",
                    (int)mdResponse.StatusCode, markdownUrl);
            }
        }
        catch (Exception ex)
        {
            // Markdown fetch failure is not fatal — we just report it as unavailable
            logger.LogWarning(ex,
                "llmstxt_compare: Exception fetching Markdown from '{MarkdownUrl}' — treating as unavailable",
                markdownUrl);
        }

        // ---------------------------------------------------------------
        // Compute metrics
        // ---------------------------------------------------------------
        logger.LogDebug("llmstxt_compare: Computing comparison metrics");

        // HTML metrics (strip tags to get "text" representation)
        var htmlText = StripHtmlTags(htmlContent);
        var htmlSize = System.Text.Encoding.UTF8.GetByteCount(htmlContent);
        var htmlTextSize = System.Text.Encoding.UTF8.GetByteCount(htmlText);
        var htmlTokenEstimate = EstimateTokenCount(htmlText);

        logger.LogDebug(
            "llmstxt_compare: HTML — raw={RawSize} bytes, text={TextSize} bytes, ~{Tokens} tokens",
            htmlSize, htmlTextSize, htmlTokenEstimate);

        // Markdown metrics
        int? markdownSize = null;
        int? markdownTokenEstimate = null;
        double? sizeRatio = null;
        double? tokenRatio = null;

        if (markdownAvailable && markdownContent != null)
        {
            markdownSize = System.Text.Encoding.UTF8.GetByteCount(markdownContent);
            markdownTokenEstimate = EstimateTokenCount(markdownContent);

            // Size ratio: how much smaller is Markdown compared to raw HTML?
            // Values < 1.0 mean Markdown is smaller (typical and expected)
            sizeRatio = markdownSize.Value > 0
                ? Math.Round((double)markdownSize.Value / htmlSize, 3)
                : 0.0;

            // Token ratio: how many fewer tokens does Markdown use vs HTML text?
            // Values < 1.0 mean Markdown uses fewer tokens (the primary benefit)
            tokenRatio = markdownTokenEstimate.Value > 0 && htmlTokenEstimate > 0
                ? Math.Round((double)markdownTokenEstimate.Value / htmlTokenEstimate, 3)
                : null;

            logger.LogDebug(
                "llmstxt_compare: Markdown — {Size} bytes, ~{Tokens} tokens",
                markdownSize, markdownTokenEstimate);
            logger.LogDebug(
                "llmstxt_compare: Ratios — size={SizeRatio}, token={TokenRatio}",
                sizeRatio, tokenRatio);
        }

        // Freshness delta: difference between Last-Modified headers
        // Positive value means Markdown is newer; negative means HTML is newer
        double? freshnessDeltaSeconds = null;
        if (htmlLastModified.HasValue && markdownLastModified.HasValue)
        {
            var delta = markdownLastModified.Value - htmlLastModified.Value;
            freshnessDeltaSeconds = Math.Round(delta.TotalSeconds, 1);

            logger.LogDebug(
                "llmstxt_compare: Freshness delta — {DeltaSeconds}s " +
                "(positive = Markdown newer, negative = HTML newer)",
                freshnessDeltaSeconds);
        }
        else
        {
            logger.LogDebug(
                "llmstxt_compare: Freshness delta unavailable " +
                "(HTML Last-Modified: {HtmlLM}, Markdown Last-Modified: {MdLM})",
                htmlLastModified.HasValue ? "present" : "absent",
                markdownLastModified.HasValue ? "present" : "absent");
        }

        // ---------------------------------------------------------------
        // Build response
        // ---------------------------------------------------------------
        logger.LogInformation(
            "llmstxt_compare: Comparison complete for '{Url}' — " +
            "markdownAvailable={MarkdownAvailable}, htmlSize={HtmlSize}, " +
            "markdownSize={MarkdownSize}, htmlTokens={HtmlTokens}, markdownTokens={MdTokens}",
            url, markdownAvailable, htmlSize,
            markdownSize?.ToString() ?? "N/A",
            htmlTokenEstimate,
            markdownTokenEstimate?.ToString() ?? "N/A");

        var response = new CompareSuccessResponse
        {
            Success = true,
            Url = url,
            MarkdownUrl = markdownUrl.ToString(),
            MarkdownAvailable = markdownAvailable,
            HtmlSize = htmlSize,
            MarkdownSize = markdownSize,
            SizeRatio = sizeRatio,
            HtmlTokenEstimate = htmlTokenEstimate,
            MarkdownTokenEstimate = markdownTokenEstimate,
            TokenRatio = tokenRatio,
            FreshnessDelta = freshnessDeltaSeconds
        };

        return SerializeResponse(response);
    }

    // ---------------------------------------------------------------
    // HTML-to-Text Conversion
    // ---------------------------------------------------------------

    /// <summary>
    /// Strips HTML tags from content to produce a plain-text representation.
    /// This simulates how retrieval pipelines process HTML before injecting
    /// it into an LLM context window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stripping process:
    /// <list type="number">
    /// <item>Remove <c>&lt;script&gt;</c> blocks (including their content)</item>
    /// <item>Remove <c>&lt;style&gt;</c> blocks (including their content)</item>
    /// <item>Remove HTML comments</item>
    /// <item>Remove all remaining HTML tags</item>
    /// <item>Decode HTML entities (e.g., <c>&amp;amp;</c> → <c>&amp;</c>)</item>
    /// <item>Normalize whitespace (collapse runs of spaces/newlines into single spaces)</item>
    /// </list>
    /// </para>
    /// <para>
    /// This is intentionally simple. A full HTML parser (like AngleSharp or
    /// HtmlAgilityPack) would be more accurate but is overkill for this use
    /// case — we're computing approximate metrics, not rendering HTML.
    /// </para>
    /// </remarks>
    /// <param name="html">The raw HTML content.</param>
    /// <returns>The stripped plain-text content.</returns>
    internal static string StripHtmlTags(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        // Step 1: Remove script blocks (content + tags)
        var result = ScriptPattern.Replace(html, " ");

        // Step 2: Remove style blocks (content + tags)
        result = StylePattern.Replace(result, " ");

        // Step 3: Remove HTML comments
        result = HtmlCommentPattern.Replace(result, " ");

        // Step 4: Remove all remaining HTML tags
        result = HtmlTagPattern.Replace(result, " ");

        // Step 5: Decode HTML entities
        result = WebUtility.HtmlDecode(result);

        // Step 6: Normalize whitespace
        result = WhitespaceNormalizationPattern.Replace(result, " ");

        return result.Trim();
    }

    // ---------------------------------------------------------------
    // Token Estimation
    // ---------------------------------------------------------------

    /// <summary>
    /// Estimates the token count of a text string using the words÷4 heuristic.
    /// This matches the default estimator used by <see cref="Core.Context.ContextGenerator"/>
    /// for consistency across the toolkit.
    /// </summary>
    /// <param name="text">The text to estimate tokens for.</param>
    /// <returns>The approximate token count.</returns>
    internal static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        // Count words by splitting on whitespace
        var words = text.Split(
            (char[]?)null,  // Split on all whitespace
            StringSplitOptions.RemoveEmptyEntries);

        // words ÷ 4, ceiling — same as ContextGenerator's default
        return (int)Math.Ceiling(words.Length / 4.0);
    }

    // ---------------------------------------------------------------
    // Serialization Helper
    // ---------------------------------------------------------------

    /// <summary>
    /// Serializes a response object to a JSON string using the shared options.
    /// </summary>
    private static string SerializeResponse<T>(T response) =>
        JsonSerializer.Serialize(response, JsonOptions);

    // ---------------------------------------------------------------
    // Response DTOs
    // ---------------------------------------------------------------

    /// <summary>
    /// JSON response for a successful comparison (even if Markdown isn't available).
    /// </summary>
    internal sealed class CompareSuccessResponse
    {
        /// <summary>Whether the comparison completed successfully.</summary>
        public bool Success { get; set; }

        /// <summary>The original URL that was compared.</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>The Markdown URL that was checked ({url}.md).</summary>
        public string MarkdownUrl { get; set; } = string.Empty;

        /// <summary>Whether a Markdown version was found at {url}.md.</summary>
        public bool MarkdownAvailable { get; set; }

        /// <summary>Size of the raw HTML content in bytes.</summary>
        public int HtmlSize { get; set; }

        /// <summary>Size of the Markdown content in bytes, or null if not available.</summary>
        public int? MarkdownSize { get; set; }

        /// <summary>
        /// Ratio of Markdown size to HTML size (Markdown / HTML).
        /// Values &lt; 1.0 indicate Markdown is smaller. Null if Markdown unavailable.
        /// </summary>
        public double? SizeRatio { get; set; }

        /// <summary>Estimated token count for the HTML-as-text content.</summary>
        public int HtmlTokenEstimate { get; set; }

        /// <summary>Estimated token count for the Markdown content, or null if unavailable.</summary>
        public int? MarkdownTokenEstimate { get; set; }

        /// <summary>
        /// Ratio of Markdown tokens to HTML-text tokens (Markdown / HTML).
        /// Values &lt; 1.0 indicate Markdown uses fewer tokens. Null if unavailable.
        /// </summary>
        public double? TokenRatio { get; set; }

        /// <summary>
        /// Difference between Last-Modified headers in seconds (Markdown - HTML).
        /// Positive = Markdown is newer, negative = HTML is newer.
        /// Null if either header is missing.
        /// </summary>
        public double? FreshnessDelta { get; set; }
    }

    /// <summary>
    /// JSON response when the comparison cannot be performed.
    /// </summary>
    internal sealed class CompareErrorResponse
    {
        /// <summary>Always false.</summary>
        public bool Success { get; set; }

        /// <summary>Human-readable error description.</summary>
        public string? ErrorMessage { get; set; }
    }
}
