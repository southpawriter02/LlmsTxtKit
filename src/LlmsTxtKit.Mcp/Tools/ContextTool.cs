using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmsTxtKit.Core.Caching;
using LlmsTxtKit.Core.Context;
using LlmsTxtKit.Core.Fetching;
using LlmsTxtKit.Core.Parsing;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace LlmsTxtKit.Mcp.Tools;

/// <summary>
/// MCP tool: <c>llmstxt_context</c>. Generates an LLM-ready context string from
/// a domain's llms.txt file by fetching all linked Markdown content, applying an
/// optional token budget, and wrapping sections in XML tags.
/// </summary>
/// <remarks>
/// <para>
/// This is the "power tool" of the LlmsTxtKit MCP server. It performs the full
/// pipeline: fetch llms.txt → parse → fetch each linked URL → clean content →
/// enforce token budget → wrap in XML → return a single ready-to-inject string.
/// An AI agent can call this tool once and get everything it needs to augment its
/// context window with the site's curated documentation.
/// </para>
/// <para>
/// The tool delegates to <see cref="ContextGenerator.GenerateAsync"/> for the
/// actual content generation. The parsed llms.txt document is obtained from cache
/// (or freshly fetched) before being passed to the generator.
/// </para>
/// <para>
/// <b>Token Budget:</b> When <c>max_tokens</c> is specified, the generator
/// enforces it by first dropping Optional sections, then truncating remaining
/// sections at sentence boundaries. The approximate token count uses a words÷4
/// heuristic (configurable in Core, but the MCP tool uses the default).
/// </para>
/// <para>
/// <b>Design Spec Reference:</b> § 3.4 (llmstxt_context tool definition).
/// </para>
/// <para>
/// <b>User Story:</b> "As an AI agent, I want to generate full LLM-ready context
/// so that I can inject curated documentation into my reasoning."
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class ContextTool
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
    // Tool Method
    // ---------------------------------------------------------------

    /// <summary>
    /// Generates an LLM-ready context string from a domain's llms.txt file.
    /// Fetches all linked Markdown, applies an optional token budget, wraps
    /// sections in XML tags, and returns the complete context with metadata.
    /// </summary>
    /// <param name="domain">
    /// The domain to generate context for (e.g., "docs.anthropic.com").
    /// The tool constructs <c>https://{domain}/llms.txt</c> automatically.
    /// </param>
    /// <param name="maxTokens">
    /// Approximate token budget for the generated context. When set, the generator
    /// drops Optional sections first, then truncates remaining sections at sentence
    /// boundaries. When <c>null</c> or 0, no limit is applied.
    /// </param>
    /// <param name="includeOptional">
    /// Whether to include the Optional section's content. Default: <c>false</c>,
    /// matching the reference implementation's behavior.
    /// </param>
    /// <param name="fetcher">
    /// The <see cref="LlmsTxtFetcher"/> service, injected by DI.
    /// </param>
    /// <param name="cache">
    /// The <see cref="LlmsTxtCache"/> service, injected by DI.
    /// </param>
    /// <param name="contextGenerator">
    /// The <see cref="ContextGenerator"/> service, injected by DI. Performs the
    /// actual content fetching, cleaning, token budgeting, and XML wrapping.
    /// </param>
    /// <param name="logger">
    /// Logger for diagnostic output (stderr per MCP convention).
    /// </param>
    /// <returns>
    /// A JSON string containing the generated context:
    /// <list type="bullet">
    /// <item><b>Success:</b> <c>{ success: true, domain, content, approximateTokenCount, sectionsIncluded, sectionsOmitted, sectionsTruncated, fetchErrors }</c></item>
    /// <item><b>Fetch Error:</b> <c>{ success: false, errorMessage: "..." }</c></item>
    /// </list>
    /// </returns>
    [McpServerTool(Name = "llmstxt_context")]
    [Description(
        "Generates a complete LLM-ready context string from a domain's /llms.txt file by " +
        "fetching all linked Markdown content, cleaning it, and wrapping sections in XML tags. " +
        "Use this to inject a site's curated documentation into your context window. " +
        "Optionally specify max_tokens to enforce a token budget (drops Optional sections first, " +
        "then truncates at sentence boundaries). Set include_optional to true to include the " +
        "Optional section. Returns the context string, approximate token count, and metadata " +
        "about which sections were included, omitted, or truncated.")]
    public static async Task<string> GenerateContext(
        [Description("The domain to generate context for (e.g., 'docs.anthropic.com'). " +
                     "Do not include 'https://' or '/llms.txt'.")]
        string domain,
        [Description("Approximate token budget. When set, content is trimmed to fit. " +
                     "0 or null means no limit.")]
        int? maxTokens = null,
        [Description("Whether to include the Optional section. Default: false.")]
        bool includeOptional = false,
        LlmsTxtFetcher? fetcher = null,
        LlmsTxtCache? cache = null,
        ContextGenerator? contextGenerator = null,
        ILogger<ContextTool>? logger = null)
    {
        var log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ContextTool>.Instance;

        // ---------------------------------------------------------------
        // Input validation
        // ---------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(domain))
        {
            log.LogWarning("llmstxt_context called with empty domain parameter");
            return SerializeResponse(new ContextErrorResponse
            {
                Success = false,
                ErrorMessage = "The 'domain' parameter is required and cannot be empty."
            });
        }

        // Normalize maxTokens: treat 0 as "no limit"
        if (maxTokens.HasValue && maxTokens.Value <= 0)
        {
            log.LogDebug("llmstxt_context: maxTokens={MaxTokens} normalized to null (no limit)", maxTokens);
            maxTokens = null;
        }

        log.LogInformation(
            "llmstxt_context: Generating context for domain '{Domain}' " +
            "(maxTokens={MaxTokens}, includeOptional={IncludeOptional})",
            domain, maxTokens?.ToString() ?? "unlimited", includeOptional);

        // ---------------------------------------------------------------
        // Obtain the parsed document (cache or origin)
        // ---------------------------------------------------------------
        var (document, fromCache, errorResponse) = await ObtainDocumentAsync(
            domain, fetcher!, cache!, log);

        if (document == null)
        {
            return errorResponse!;
        }

        // ---------------------------------------------------------------
        // Generate context using ContextGenerator
        // ---------------------------------------------------------------
        var contextOptions = new ContextOptions
        {
            MaxTokens = maxTokens,
            IncludeOptional = includeOptional,
            WrapSectionsInXml = true  // Always wrap in XML for MCP tool output
        };

        log.LogDebug(
            "llmstxt_context: Starting context generation for '{Domain}' " +
            "(title='{Title}', {SectionCount} sections)",
            domain, document.Title, document.Sections.Count);

        ContextResult contextResult;
        try
        {
            contextResult = await contextGenerator!.GenerateAsync(document, contextOptions);
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "llmstxt_context: Unexpected exception during context generation for '{Domain}'",
                domain);
            return SerializeResponse(new ContextErrorResponse
            {
                Success = false,
                ErrorMessage = $"Unexpected error during context generation: {ex.Message}"
            });
        }

        // ---------------------------------------------------------------
        // Log generation results
        // ---------------------------------------------------------------
        log.LogInformation(
            "llmstxt_context: Context generated for '{Domain}' — " +
            "{TokenCount} tokens, {IncludedCount} sections included, " +
            "{OmittedCount} omitted, {TruncatedCount} truncated, " +
            "{FetchErrorCount} fetch errors, fromCache={FromCache}",
            domain,
            contextResult.ApproximateTokenCount,
            contextResult.SectionsIncluded.Count,
            contextResult.SectionsOmitted.Count,
            contextResult.SectionsTruncated.Count,
            contextResult.FetchErrors.Count,
            fromCache);

        if (contextResult.SectionsOmitted.Count > 0)
        {
            log.LogDebug(
                "llmstxt_context: Omitted sections for '{Domain}': [{Omitted}]",
                domain, string.Join(", ", contextResult.SectionsOmitted));
        }

        if (contextResult.SectionsTruncated.Count > 0)
        {
            log.LogDebug(
                "llmstxt_context: Truncated sections for '{Domain}': [{Truncated}]",
                domain, string.Join(", ", contextResult.SectionsTruncated));
        }

        if (contextResult.FetchErrors.Count > 0)
        {
            log.LogWarning(
                "llmstxt_context: Fetch errors for '{Domain}': [{Errors}]",
                domain,
                string.Join("; ", contextResult.FetchErrors.Select(e => $"{e.Url}: {e.ErrorMessage}")));
        }

        // ---------------------------------------------------------------
        // Build the success response
        // ---------------------------------------------------------------
        var fetchErrorDtos = contextResult.FetchErrors.Count > 0
            ? contextResult.FetchErrors.Select(e => new ContextFetchError
            {
                Url = e.Url.ToString(),
                Error = e.ErrorMessage
            }).ToList()
            : null;

        var response = new ContextSuccessResponse
        {
            Success = true,
            Domain = domain,
            Content = contextResult.Content,
            ApproximateTokenCount = contextResult.ApproximateTokenCount,
            SectionsIncluded = contextResult.SectionsIncluded.ToList(),
            SectionsOmitted = contextResult.SectionsOmitted.Count > 0
                ? contextResult.SectionsOmitted.ToList()
                : null,
            SectionsTruncated = contextResult.SectionsTruncated.Count > 0
                ? contextResult.SectionsTruncated.ToList()
                : null,
            FetchErrors = fetchErrorDtos,
            FromCache = fromCache
        };

        return SerializeResponse(response);
    }

    // ---------------------------------------------------------------
    // Document Retrieval Helper
    // ---------------------------------------------------------------

    /// <summary>
    /// Obtains a parsed llms.txt document for the given domain, first checking
    /// the cache, then falling back to a fresh fetch.
    /// </summary>
    private static async Task<(LlmsDocument? Document, bool FromCache, string? ErrorResponse)> ObtainDocumentAsync(
        string domain,
        LlmsTxtFetcher fetcher,
        LlmsTxtCache cache,
        ILogger logger)
    {
        // Check cache first
        var cached = await cache.GetAsync(domain);
        if (cached != null && !cached.IsExpired())
        {
            logger.LogDebug(
                "llmstxt_context: Cache hit for '{Domain}' (fetched at {FetchedAt})",
                domain, cached.FetchedAt);
            return (cached.Document, true, null);
        }

        // Fetch from origin
        FetchResult result;
        try
        {
            logger.LogDebug("llmstxt_context: Fetching https://{Domain}/llms.txt", domain);
            result = await fetcher.FetchAsync(domain);
            logger.LogDebug(
                "llmstxt_context: Fetch completed for '{Domain}' — Status={Status}",
                domain, result.Status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "llmstxt_context: Unexpected exception fetching '{Domain}'", domain);
            var errorResp = SerializeResponse(new ContextErrorResponse
            {
                Success = false,
                ErrorMessage = $"Unexpected error while fetching llms.txt: {ex.Message}"
            });
            return (null, false, errorResp);
        }

        // Handle non-success statuses
        if (result.Status != FetchStatus.Success)
        {
            var statusMessage = result.Status switch
            {
                FetchStatus.NotFound =>
                    $"No /llms.txt file found at https://{domain}/llms.txt (HTTP 404).",
                FetchStatus.Blocked =>
                    $"Access to https://{domain}/llms.txt was blocked by a Web Application Firewall.",
                FetchStatus.RateLimited =>
                    $"Rate limited by https://{domain}. Please try again later.",
                FetchStatus.DnsFailure =>
                    $"Could not resolve DNS for '{domain}'.",
                FetchStatus.Timeout =>
                    $"Request to https://{domain}/llms.txt timed out.",
                _ => result.ErrorMessage ?? "An unknown error occurred."
            };

            logger.LogWarning(
                "llmstxt_context: Cannot generate context — {Status} for '{Domain}': {Message}",
                result.Status, domain, statusMessage);

            var errorResp = SerializeResponse(new ContextErrorResponse
            {
                Success = false,
                Status = result.Status.ToString(),
                ErrorMessage = statusMessage
            });
            return (null, false, errorResp);
        }

        // Cache the successful result
        var cacheEntry = new CacheEntry
        {
            Document = result.Document!,
            FetchedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            HttpHeaders = result.ResponseHeaders != null
                ? new Dictionary<string, string>(result.ResponseHeaders)
                : null,
            FetchResult = result
        };
        await cache.SetAsync(domain, cacheEntry);

        logger.LogDebug(
            "llmstxt_context: Cached document for '{Domain}' — Title='{Title}'",
            domain, result.Document!.Title);

        return (result.Document, false, null);
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
    /// JSON response for successful context generation.
    /// </summary>
    internal sealed class ContextSuccessResponse
    {
        /// <summary>Whether context generation succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>The domain context was generated for.</summary>
        public string Domain { get; set; } = string.Empty;

        /// <summary>The generated LLM-ready context string.</summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>Approximate token count of the generated content.</summary>
        public int ApproximateTokenCount { get; set; }

        /// <summary>Sections that were fully included in the context.</summary>
        public List<string> SectionsIncluded { get; set; } = new();

        /// <summary>Sections that were dropped entirely (budget or Optional exclusion).</summary>
        public List<string>? SectionsOmitted { get; set; }

        /// <summary>Sections that were truncated at sentence boundaries.</summary>
        public List<string>? SectionsTruncated { get; set; }

        /// <summary>URLs that failed to fetch during content retrieval.</summary>
        public List<ContextFetchError>? FetchErrors { get; set; }

        /// <summary>Whether the llms.txt document was served from cache.</summary>
        public bool FromCache { get; set; }
    }

    /// <summary>
    /// JSON response when context generation cannot be performed.
    /// </summary>
    internal sealed class ContextErrorResponse
    {
        /// <summary>Always false.</summary>
        public bool Success { get; set; }

        /// <summary>The fetch status code, if the failure was during llms.txt fetch.</summary>
        public string? Status { get; set; }

        /// <summary>Human-readable error description.</summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// A single fetch error in the context response.
    /// </summary>
    internal sealed class ContextFetchError
    {
        /// <summary>The URL that failed to fetch.</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>Human-readable error message.</summary>
        public string Error { get; set; } = string.Empty;
    }
}
