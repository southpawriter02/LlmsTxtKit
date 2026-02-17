using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmsTxtKit.Core.Caching;
using LlmsTxtKit.Core.Fetching;
using LlmsTxtKit.Core.Parsing;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace LlmsTxtKit.Mcp.Tools;

/// <summary>
/// MCP tool: <c>llmstxt_discover</c>. Given a domain, checks for the existence of
/// an <c>/llms.txt</c> file and returns the parsed document structure if found.
/// </summary>
/// <remarks>
/// <para>
/// This is the "entry point" tool for any AI agent interaction with LlmsTxtKit.
/// An agent should call <c>llmstxt_discover</c> first to check whether a site
/// provides curated llms.txt content before deciding to fetch sections, validate,
/// or generate context.
/// </para>
/// <para>
/// The tool delegates to <see cref="LlmsTxtFetcher.FetchAsync"/> for the actual
/// HTTP fetch, and optionally stores the result in <see cref="LlmsTxtCache"/> for
/// subsequent tool calls (so <c>llmstxt_validate</c>, <c>llmstxt_context</c>, etc.
/// can reuse the fetched document without hitting the origin server again).
/// </para>
/// <para>
/// <b>Design Spec Reference:</b> § 3.1 (llmstxt_discover tool definition).
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class DiscoverTool
{
    // ---------------------------------------------------------------
    // JSON Serialization Options
    // ---------------------------------------------------------------

    /// <summary>
    /// Shared JSON serialization options for tool responses. Uses camelCase
    /// property naming to align with MCP convention (JSON responses from tools
    /// are typically consumed by AI agents expecting camelCase).
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
    /// Checks whether a domain has an <c>/llms.txt</c> file and returns its
    /// parsed structure. This is the primary discovery mechanism for AI agents
    /// to determine if a site supports llms.txt-curated content.
    /// </summary>
    /// <param name="domain">
    /// The domain to check (e.g., "docs.anthropic.com", "fasthtml.org").
    /// The tool constructs the URL <c>https://{domain}/llms.txt</c> automatically.
    /// </param>
    /// <param name="fetcher">
    /// The <see cref="LlmsTxtFetcher"/> service, injected by the MCP server's DI container.
    /// Handles HTTP fetching with WAF detection, retry logic, and timeout handling.
    /// </param>
    /// <param name="cache">
    /// The <see cref="LlmsTxtCache"/> service, injected by DI. Caches fetch results
    /// so subsequent tool calls (validate, context, etc.) don't re-fetch.
    /// </param>
    /// <param name="logger">
    /// Logger for diagnostic output. Logs are written to stderr (per MCP convention)
    /// so they don't interfere with the JSON-RPC transport on stdout.
    /// </param>
    /// <returns>
    /// A JSON string containing the discovery result. The structure varies based
    /// on the fetch outcome:
    /// <list type="bullet">
    /// <item><b>Found:</b> <c>{ found: true, status: "success", title, summary, sections: [...], diagnostics: [...] }</c></item>
    /// <item><b>Not Found:</b> <c>{ found: false, status: "notFound", message: "..." }</c></item>
    /// <item><b>Blocked:</b> <c>{ found: false, status: "blocked", blockReason: "..." }</c></item>
    /// <item><b>Error:</b> <c>{ found: false, status: "...", errorMessage: "..." }</c></item>
    /// </list>
    /// </returns>
    [McpServerTool(Name = "llmstxt_discover")]
    [Description(
        "Given a domain name, checks whether the site has an /llms.txt file and returns " +
        "its parsed structure. Use this tool first to discover if a site provides curated " +
        "AI-readable documentation before attempting to fetch sections or generate context. " +
        "Returns the document title, summary, section names with entry counts, and any " +
        "parse diagnostics. If the site doesn't have an llms.txt file, returns the " +
        "fetch status (not found, blocked by WAF, rate limited, etc.).")]
    public static async Task<string> Discover(
        [Description("The domain to check for an llms.txt file (e.g., 'docs.anthropic.com'). " +
                     "Do not include 'https://' or '/llms.txt' — the tool constructs the full URL.")]
        string domain,
        LlmsTxtFetcher fetcher,
        LlmsTxtCache cache,
        ILogger<DiscoverTool> logger)
    {
        // ---------------------------------------------------------------
        // Input validation
        // ---------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(domain))
        {
            logger.LogWarning("llmstxt_discover called with empty domain parameter");
            return SerializeResponse(new DiscoverErrorResponse
            {
                Found = false,
                Status = "error",
                ErrorMessage = "The 'domain' parameter is required and cannot be empty."
            });
        }

        logger.LogInformation("llmstxt_discover: Checking domain '{Domain}' for /llms.txt", domain);

        // ---------------------------------------------------------------
        // Check cache first
        // ---------------------------------------------------------------
        var cached = await cache.GetAsync(domain);
        if (cached != null && !cached.IsExpired())
        {
            logger.LogDebug("llmstxt_discover: Cache hit for '{Domain}' (fetched at {FetchedAt})",
                domain, cached.FetchedAt);
            return BuildSuccessResponse(cached.Document, domain, logger, fromCache: true);
        }

        // ---------------------------------------------------------------
        // Fetch from origin
        // ---------------------------------------------------------------
        FetchResult result;
        try
        {
            logger.LogDebug("llmstxt_discover: Fetching https://{Domain}/llms.txt", domain);
            result = await fetcher.FetchAsync(domain);
            logger.LogDebug(
                "llmstxt_discover: Fetch completed for '{Domain}' — Status={Status}, Duration={Duration}ms",
                domain, result.Status, result.Duration.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "llmstxt_discover: Unexpected exception fetching '{Domain}'", domain);
            return SerializeResponse(new DiscoverErrorResponse
            {
                Found = false,
                Status = "error",
                ErrorMessage = $"Unexpected error while fetching: {ex.Message}"
            });
        }

        // ---------------------------------------------------------------
        // Process fetch result
        // ---------------------------------------------------------------
        switch (result.Status)
        {
            case FetchStatus.Success:
                // Cache the successful result for subsequent tool calls
                var cacheEntry = new CacheEntry
                {
                    Document = result.Document!,
                    FetchedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), // Uses default TTL
                    HttpHeaders = result.ResponseHeaders != null
                        ? new Dictionary<string, string>(result.ResponseHeaders)
                        : null,
                    FetchResult = result
                };
                await cache.SetAsync(domain, cacheEntry);
                logger.LogInformation(
                    "llmstxt_discover: Found llms.txt for '{Domain}' — Title='{Title}', Sections={SectionCount}",
                    domain, result.Document!.Title, result.Document.Sections.Count);

                return BuildSuccessResponse(result.Document, domain, logger, fromCache: false);

            case FetchStatus.NotFound:
                logger.LogInformation("llmstxt_discover: No llms.txt found for '{Domain}' (404)", domain);
                return SerializeResponse(new DiscoverErrorResponse
                {
                    Found = false,
                    Status = "notFound",
                    ErrorMessage = $"No /llms.txt file found at https://{domain}/llms.txt (HTTP 404)."
                });

            case FetchStatus.Blocked:
                logger.LogWarning(
                    "llmstxt_discover: Blocked by WAF for '{Domain}' — Reason: {BlockReason}",
                    domain, result.BlockReason ?? "Unknown");
                return SerializeResponse(new DiscoverErrorResponse
                {
                    Found = false,
                    Status = "blocked",
                    BlockReason = result.BlockReason,
                    ErrorMessage = $"Access to https://{domain}/llms.txt was blocked by a Web Application Firewall."
                });

            case FetchStatus.RateLimited:
                logger.LogWarning("llmstxt_discover: Rate limited for '{Domain}', RetryAfter={RetryAfter}",
                    domain, result.RetryAfter);
                return SerializeResponse(new DiscoverErrorResponse
                {
                    Found = false,
                    Status = "rateLimited",
                    ErrorMessage = $"Rate limited by https://{domain}. " +
                        (result.RetryAfter.HasValue
                            ? $"Retry after {result.RetryAfter.Value.TotalSeconds:F0} seconds."
                            : "Please try again later.")
                });

            case FetchStatus.DnsFailure:
                logger.LogWarning("llmstxt_discover: DNS failure for '{Domain}'", domain);
                return SerializeResponse(new DiscoverErrorResponse
                {
                    Found = false,
                    Status = "dnsFailure",
                    ErrorMessage = $"Could not resolve DNS for '{domain}'. Verify the domain name is correct."
                });

            case FetchStatus.Timeout:
                logger.LogWarning("llmstxt_discover: Timeout for '{Domain}'", domain);
                return SerializeResponse(new DiscoverErrorResponse
                {
                    Found = false,
                    Status = "timeout",
                    ErrorMessage = $"Request to https://{domain}/llms.txt timed out."
                });

            default: // FetchStatus.Error or unknown
                logger.LogWarning(
                    "llmstxt_discover: Error fetching '{Domain}' — {ErrorMessage}",
                    domain, result.ErrorMessage);
                return SerializeResponse(new DiscoverErrorResponse
                {
                    Found = false,
                    Status = "error",
                    ErrorMessage = result.ErrorMessage ?? "An unknown error occurred."
                });
        }
    }

    // ---------------------------------------------------------------
    // Response Builders
    // ---------------------------------------------------------------

    /// <summary>
    /// Builds a successful discovery response from a parsed <see cref="LlmsDocument"/>.
    /// </summary>
    private static string BuildSuccessResponse(
        LlmsDocument document, string domain, ILogger logger, bool fromCache)
    {
        var sections = document.Sections.Select(s => new DiscoverSection
        {
            Name = s.Name,
            EntryCount = s.Entries.Count,
            IsOptional = s.IsOptional
        }).ToList();

        var diagnostics = document.Diagnostics.Select(d => new DiscoverDiagnostic
        {
            Severity = d.Severity.ToString().ToLowerInvariant(),
            Message = d.Message,
            Line = d.Line
        }).ToList();

        logger.LogDebug(
            "llmstxt_discover: Building success response for '{Domain}' — " +
            "{SectionCount} sections, {DiagnosticCount} diagnostics, fromCache={FromCache}",
            domain, sections.Count, diagnostics.Count, fromCache);

        var response = new DiscoverSuccessResponse
        {
            Found = true,
            Status = "success",
            Title = document.Title,
            Summary = document.Summary,
            Sections = sections,
            Diagnostics = diagnostics.Count > 0 ? diagnostics : null,
            FromCache = fromCache
        };

        return SerializeResponse(response);
    }

    /// <summary>
    /// Serializes a response object to a JSON string using the shared options.
    /// </summary>
    private static string SerializeResponse<T>(T response) =>
        JsonSerializer.Serialize(response, JsonOptions);

    // ---------------------------------------------------------------
    // Response DTOs
    // ---------------------------------------------------------------
    // These are internal DTOs used solely for JSON serialization of tool
    // responses. They are intentionally simple and don't carry behavior.
    // ---------------------------------------------------------------

    /// <summary>
    /// JSON response for a successful llmstxt_discover result (site has llms.txt).
    /// </summary>
    internal sealed class DiscoverSuccessResponse
    {
        /// <summary>Whether an llms.txt file was found.</summary>
        public bool Found { get; set; }

        /// <summary>The fetch status string (always "success" for this DTO).</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>The document title (H1 heading).</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>The document summary (blockquote), or null if none.</summary>
        public string? Summary { get; set; }

        /// <summary>Array of sections with their names, entry counts, and optional flags.</summary>
        public List<DiscoverSection> Sections { get; set; } = new();

        /// <summary>Parse diagnostics (warnings/errors), or null if clean.</summary>
        public List<DiscoverDiagnostic>? Diagnostics { get; set; }

        /// <summary>Whether the result was served from cache.</summary>
        public bool FromCache { get; set; }
    }

    /// <summary>
    /// JSON response for a failed llmstxt_discover result (site doesn't have llms.txt,
    /// was blocked, timed out, etc.).
    /// </summary>
    internal sealed class DiscoverErrorResponse
    {
        /// <summary>Always false for error responses.</summary>
        public bool Found { get; set; }

        /// <summary>The classified status (notFound, blocked, rateLimited, dnsFailure, timeout, error).</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Human-readable error description.</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>WAF vendor diagnosis, populated only when status is "blocked".</summary>
        public string? BlockReason { get; set; }
    }

    /// <summary>
    /// Section summary included in the discovery response.
    /// </summary>
    internal sealed class DiscoverSection
    {
        /// <summary>The section name (H2 heading text).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>How many entries (links) are in this section.</summary>
        public int EntryCount { get; set; }

        /// <summary>Whether this is the Optional section.</summary>
        public bool IsOptional { get; set; }
    }

    /// <summary>
    /// Parse diagnostic included in the discovery response.
    /// </summary>
    internal sealed class DiscoverDiagnostic
    {
        /// <summary>Diagnostic severity ("warning" or "error").</summary>
        public string Severity { get; set; } = string.Empty;

        /// <summary>Human-readable diagnostic message.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>The line number in the raw content, or null if not applicable.</summary>
        public int? Line { get; set; }
    }
}
