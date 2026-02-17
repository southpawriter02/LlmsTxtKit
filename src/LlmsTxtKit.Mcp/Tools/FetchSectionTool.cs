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
/// MCP tool: <c>llmstxt_fetch_section</c>. Given a domain and section name,
/// fetches the linked Markdown content for every entry in that section and
/// returns it as a structured JSON array.
/// </summary>
/// <remarks>
/// <para>
/// This tool enables AI agents to retrieve the actual documentation content
/// from a specific section of a site's llms.txt file. While <c>llmstxt_discover</c>
/// tells the agent what sections exist, <c>llmstxt_fetch_section</c> retrieves
/// the content behind those section entries.
/// </para>
/// <para>
/// The tool first obtains the parsed llms.txt document (from cache or by fetching),
/// locates the named section, then sequentially fetches each linked URL's content
/// using the injected <see cref="IContentFetcher"/>. Content is cleaned (HTML
/// comments and base64 images stripped) before being returned.
/// </para>
/// <para>
/// <b>Design Spec Reference:</b> § 3.2 (llmstxt_fetch_section tool definition).
/// </para>
/// <para>
/// <b>User Story:</b> "As an AI agent, I want to fetch a specific section's content
/// so that I can retrieve only the documentation relevant to the user's question."
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class FetchSectionTool
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
    /// Fetches the linked Markdown content for every entry in a named section
    /// of a domain's llms.txt file. Returns the content as a structured JSON
    /// array with title, URL, and content (or error) for each entry.
    /// </summary>
    /// <param name="domain">
    /// The domain whose llms.txt to read (e.g., "docs.anthropic.com").
    /// The tool constructs the URL <c>https://{domain}/llms.txt</c> automatically.
    /// </param>
    /// <param name="section">
    /// The section name to fetch content for (e.g., "Docs", "API", "Optional").
    /// Must match an H2 heading in the llms.txt file (case-insensitive comparison).
    /// </param>
    /// <param name="fetcher">
    /// The <see cref="LlmsTxtFetcher"/> service, injected by the MCP server's DI container.
    /// Used to fetch the llms.txt file from the origin server if not cached.
    /// </param>
    /// <param name="cache">
    /// The <see cref="LlmsTxtCache"/> service, injected by DI. Caches fetch results
    /// so repeated section fetches don't re-fetch the llms.txt file itself.
    /// </param>
    /// <param name="contentFetcher">
    /// The <see cref="IContentFetcher"/> service, injected by DI. Used to fetch the
    /// actual Markdown content of each linked URL within the section.
    /// </param>
    /// <param name="logger">
    /// Logger for diagnostic output. Logs are written to stderr (per MCP convention)
    /// so they don't interfere with the JSON-RPC transport on stdout.
    /// </param>
    /// <returns>
    /// A JSON string containing the fetch result. The structure varies:
    /// <list type="bullet">
    /// <item><b>Success:</b> <c>{ success: true, domain, section, entryCount, entries: [{ title, url, content?, error? }], fromCache }</c></item>
    /// <item><b>Section Not Found:</b> <c>{ success: false, errorMessage, availableSections: [...] }</c></item>
    /// <item><b>Domain Error:</b> <c>{ success: false, errorMessage, status }</c></item>
    /// </list>
    /// </returns>
    [McpServerTool(Name = "llmstxt_fetch_section")]
    [Description(
        "Given a domain and section name, fetches the linked Markdown content for every " +
        "entry in that section of the site's /llms.txt file. Use this after llmstxt_discover " +
        "to retrieve the actual documentation content for a specific section. Returns each " +
        "entry's title, URL, and fetched content (or error details if the fetch failed). " +
        "Section names are matched case-insensitively against the H2 headings in the llms.txt file.")]
    public static async Task<string> FetchSection(
        [Description("The domain to fetch section content from (e.g., 'docs.anthropic.com'). " +
                     "Do not include 'https://' or '/llms.txt'.")]
        string domain,
        [Description("The section name to fetch (e.g., 'Docs', 'API', 'Optional'). " +
                     "Must match an H2 heading in the llms.txt file. Case-insensitive.")]
        string section,
        LlmsTxtFetcher fetcher,
        LlmsTxtCache cache,
        IContentFetcher contentFetcher,
        ILogger<FetchSectionTool> logger)
    {
        // ---------------------------------------------------------------
        // Input validation
        // ---------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(domain))
        {
            logger.LogWarning("llmstxt_fetch_section called with empty domain parameter");
            return SerializeResponse(new FetchSectionErrorResponse
            {
                Success = false,
                ErrorMessage = "The 'domain' parameter is required and cannot be empty."
            });
        }

        if (string.IsNullOrWhiteSpace(section))
        {
            logger.LogWarning("llmstxt_fetch_section called with empty section parameter for domain '{Domain}'", domain);
            return SerializeResponse(new FetchSectionErrorResponse
            {
                Success = false,
                ErrorMessage = "The 'section' parameter is required and cannot be empty."
            });
        }

        logger.LogInformation(
            "llmstxt_fetch_section: Fetching section '{Section}' from domain '{Domain}'",
            section, domain);

        // ---------------------------------------------------------------
        // Obtain the parsed document (cache or origin)
        // ---------------------------------------------------------------
        var (document, fromCache, errorResponse) = await ObtainDocumentAsync(
            domain, fetcher, cache, logger);

        if (document == null)
        {
            // errorResponse is already populated with the fetch failure details
            return errorResponse!;
        }

        // ---------------------------------------------------------------
        // Locate the named section (case-insensitive)
        // ---------------------------------------------------------------
        var matchedSection = document.Sections.FirstOrDefault(
            s => string.Equals(s.Name, section, StringComparison.OrdinalIgnoreCase));

        if (matchedSection == null)
        {
            var availableSections = document.Sections.Select(s => s.Name).ToList();
            logger.LogWarning(
                "llmstxt_fetch_section: Section '{Section}' not found in '{Domain}'. " +
                "Available sections: [{Available}]",
                section, domain, string.Join(", ", availableSections));

            return SerializeResponse(new FetchSectionErrorResponse
            {
                Success = false,
                ErrorMessage = $"Section '{section}' not found in the llms.txt file for '{domain}'.",
                AvailableSections = availableSections
            });
        }

        logger.LogDebug(
            "llmstxt_fetch_section: Found section '{Section}' with {EntryCount} entries in '{Domain}'",
            matchedSection.Name, matchedSection.Entries.Count, domain);

        // ---------------------------------------------------------------
        // Fetch content for each entry in the section
        // ---------------------------------------------------------------
        var entries = new List<FetchSectionEntry>();
        var fetchErrorCount = 0;

        for (int i = 0; i < matchedSection.Entries.Count; i++)
        {
            var entry = matchedSection.Entries[i];
            logger.LogDebug(
                "llmstxt_fetch_section: Fetching entry {Index}/{Total}: '{Title}' at {Url}",
                i + 1, matchedSection.Entries.Count, entry.Title ?? "(untitled)", entry.Url);

            try
            {
                var fetchResult = await contentFetcher.FetchContentAsync(entry.Url);

                if (fetchResult.IsSuccess)
                {
                    // Clean the content (strip HTML comments, base64 images) using
                    // ContextGenerator's internal CleanContent method
                    var cleaned = ContextGenerator.CleanContent(fetchResult.Content!);

                    logger.LogDebug(
                        "llmstxt_fetch_section: Successfully fetched '{Title}' — " +
                        "{RawLength} chars raw, {CleanedLength} chars cleaned",
                        entry.Title ?? "(untitled)", fetchResult.Content!.Length, cleaned.Length);

                    entries.Add(new FetchSectionEntry
                    {
                        Title = entry.Title,
                        Url = entry.Url.ToString(),
                        Description = entry.Description,
                        Content = cleaned
                    });
                }
                else
                {
                    fetchErrorCount++;
                    logger.LogWarning(
                        "llmstxt_fetch_section: Failed to fetch '{Title}' at {Url}: {Error}",
                        entry.Title ?? "(untitled)", entry.Url, fetchResult.ErrorMessage);

                    entries.Add(new FetchSectionEntry
                    {
                        Title = entry.Title,
                        Url = entry.Url.ToString(),
                        Description = entry.Description,
                        Error = fetchResult.ErrorMessage
                    });
                }
            }
            catch (Exception ex)
            {
                fetchErrorCount++;
                logger.LogError(ex,
                    "llmstxt_fetch_section: Unexpected exception fetching '{Title}' at {Url}",
                    entry.Title ?? "(untitled)", entry.Url);

                entries.Add(new FetchSectionEntry
                {
                    Title = entry.Title,
                    Url = entry.Url.ToString(),
                    Description = entry.Description,
                    Error = $"Unexpected error: {ex.Message}"
                });
            }
        }

        logger.LogInformation(
            "llmstxt_fetch_section: Completed fetching section '{Section}' from '{Domain}' — " +
            "{SuccessCount}/{TotalCount} entries fetched successfully (fromCache={FromCache})",
            matchedSection.Name, domain,
            matchedSection.Entries.Count - fetchErrorCount, matchedSection.Entries.Count,
            fromCache);

        // ---------------------------------------------------------------
        // Build and return the success response
        // ---------------------------------------------------------------
        var response = new FetchSectionSuccessResponse
        {
            Success = true,
            Domain = domain,
            Section = matchedSection.Name,
            EntryCount = matchedSection.Entries.Count,
            Entries = entries,
            FetchErrors = fetchErrorCount > 0 ? fetchErrorCount : null,
            FromCache = fromCache
        };

        return SerializeResponse(response);
    }

    // ---------------------------------------------------------------
    // Document Retrieval Helper
    // ---------------------------------------------------------------

    /// <summary>
    /// Obtains a parsed llms.txt document for the given domain, first checking
    /// the cache, then falling back to a fresh fetch. Returns the document, a
    /// "from cache" flag, and an error response string if the fetch failed.
    /// </summary>
    /// <remarks>
    /// This pattern is shared between <see cref="FetchSectionTool"/>,
    /// <see cref="ValidateTool"/>, and <see cref="ContextTool"/>. Each tool
    /// needs the parsed document but handles the "not found" case differently
    /// in terms of response structure, so this method returns the raw components
    /// rather than a pre-formatted response.
    /// </remarks>
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
                "llmstxt_fetch_section: Cache hit for '{Domain}' (fetched at {FetchedAt})",
                domain, cached.FetchedAt);
            return (cached.Document, true, null);
        }

        // Fetch from origin
        FetchResult result;
        try
        {
            logger.LogDebug("llmstxt_fetch_section: Fetching https://{Domain}/llms.txt", domain);
            result = await fetcher.FetchAsync(domain);
            logger.LogDebug(
                "llmstxt_fetch_section: Fetch completed for '{Domain}' — Status={Status}",
                domain, result.Status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "llmstxt_fetch_section: Unexpected exception fetching '{Domain}'", domain);
            var errorResp = SerializeResponse(new FetchSectionErrorResponse
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
                    $"Access to https://{domain}/llms.txt was blocked by a Web Application Firewall. " +
                    (result.BlockReason != null ? $"Block reason: {result.BlockReason}" : ""),
                FetchStatus.RateLimited =>
                    $"Rate limited by https://{domain}. " +
                    (result.RetryAfter.HasValue
                        ? $"Retry after {result.RetryAfter.Value.TotalSeconds:F0} seconds."
                        : "Please try again later."),
                FetchStatus.DnsFailure =>
                    $"Could not resolve DNS for '{domain}'. Verify the domain name is correct.",
                FetchStatus.Timeout =>
                    $"Request to https://{domain}/llms.txt timed out.",
                _ => result.ErrorMessage ?? "An unknown error occurred."
            };

            logger.LogWarning(
                "llmstxt_fetch_section: Cannot fetch section — {Status} for '{Domain}': {Message}",
                result.Status, domain, statusMessage);

            var errorResp = SerializeResponse(new FetchSectionErrorResponse
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
            "llmstxt_fetch_section: Cached document for '{Domain}' — Title='{Title}'",
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
    // These are internal DTOs used solely for JSON serialization of tool
    // responses. They are intentionally simple and don't carry behavior.
    // ---------------------------------------------------------------

    /// <summary>
    /// JSON response for a successful llmstxt_fetch_section result.
    /// Contains the section's entries with their fetched content.
    /// </summary>
    internal sealed class FetchSectionSuccessResponse
    {
        /// <summary>Whether the section was successfully fetched.</summary>
        public bool Success { get; set; }

        /// <summary>The domain the section was fetched from.</summary>
        public string Domain { get; set; } = string.Empty;

        /// <summary>The section name (as it appears in the llms.txt H2 heading).</summary>
        public string Section { get; set; } = string.Empty;

        /// <summary>How many entries are in this section.</summary>
        public int EntryCount { get; set; }

        /// <summary>The fetched entries with their content or error details.</summary>
        public List<FetchSectionEntry> Entries { get; set; } = new();

        /// <summary>Number of entries that failed to fetch, or null if all succeeded.</summary>
        public int? FetchErrors { get; set; }

        /// <summary>Whether the llms.txt document itself was served from cache.</summary>
        public bool FromCache { get; set; }
    }

    /// <summary>
    /// JSON response for a failed llmstxt_fetch_section result.
    /// </summary>
    internal sealed class FetchSectionErrorResponse
    {
        /// <summary>Always false for error responses.</summary>
        public bool Success { get; set; }

        /// <summary>The fetch status code, if the failure was during llms.txt fetch.</summary>
        public string? Status { get; set; }

        /// <summary>Human-readable error description.</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Available section names, populated when the requested section doesn't exist.
        /// Helps the agent correct its request without re-calling llmstxt_discover.
        /// </summary>
        public List<string>? AvailableSections { get; set; }
    }

    /// <summary>
    /// A single entry within the fetch section response, containing either
    /// the fetched content or an error message.
    /// </summary>
    internal sealed class FetchSectionEntry
    {
        /// <summary>The entry's link text (title), or null if untitled.</summary>
        public string? Title { get; set; }

        /// <summary>The entry's URL.</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>The entry's description (after the colon), or null.</summary>
        public string? Description { get; set; }

        /// <summary>The fetched and cleaned Markdown content, or null if fetch failed.</summary>
        public string? Content { get; set; }

        /// <summary>Error message if the content fetch failed, or null on success.</summary>
        public string? Error { get; set; }
    }
}
