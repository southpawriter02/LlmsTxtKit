using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmsTxtKit.Core.Caching;
using LlmsTxtKit.Core.Fetching;
using LlmsTxtKit.Core.Parsing;
using LlmsTxtKit.Core.Validation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace LlmsTxtKit.Mcp.Tools;

/// <summary>
/// MCP tool: <c>llmstxt_validate</c>. Runs full spec-compliance validation on
/// a domain's llms.txt file and returns a structured validation report with
/// errors, warnings, and an overall <c>isValid</c> flag.
/// </summary>
/// <remarks>
/// <para>
/// This tool lets AI agents assess the quality and compliance of a site's
/// llms.txt file before relying on its content. A valid file (zero errors)
/// can be trusted for context generation; a file with warnings may still be
/// usable but the agent should note the quality concerns.
/// </para>
/// <para>
/// The tool delegates to <see cref="LlmsDocumentValidator"/> for the actual
/// validation logic. By default only structural rules run (fast, no network).
/// Optional <c>check_urls</c> and <c>check_freshness</c> parameters enable
/// network-dependent checks.
/// </para>
/// <para>
/// <b>Design Spec Reference:</b> § 3.3 (llmstxt_validate tool definition).
/// </para>
/// <para>
/// <b>User Story:</b> "As an AI agent, I want to validate a site's llms.txt
/// compliance so that I can assess confidence in the content before using it."
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class ValidateTool
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
    /// Validates a domain's llms.txt file against the specification and returns
    /// a structured report with errors, warnings, and an overall validity flag.
    /// </summary>
    /// <param name="domain">
    /// The domain to validate (e.g., "docs.anthropic.com").
    /// The tool constructs the URL <c>https://{domain}/llms.txt</c> automatically.
    /// </param>
    /// <param name="checkUrls">
    /// Whether to verify that linked URLs are reachable (HTTP HEAD checks).
    /// Default: <c>false</c>. Enabling this makes the validation slower but
    /// catches broken links.
    /// </param>
    /// <param name="checkFreshness">
    /// Whether to compare Last-Modified headers to detect stale content.
    /// Default: <c>false</c>. Enabling this adds network overhead.
    /// </param>
    /// <param name="fetcher">
    /// The <see cref="LlmsTxtFetcher"/> service, injected by DI.
    /// </param>
    /// <param name="cache">
    /// The <see cref="LlmsTxtCache"/> service, injected by DI.
    /// </param>
    /// <param name="validator">
    /// The <see cref="LlmsDocumentValidator"/> service, injected by DI.
    /// Runs all registered validation rules against the parsed document.
    /// </param>
    /// <param name="logger">
    /// Logger for diagnostic output (stderr per MCP convention).
    /// </param>
    /// <returns>
    /// A JSON string containing the validation result:
    /// <list type="bullet">
    /// <item><b>Valid:</b> <c>{ isValid: true, errorCount: 0, warningCount: 0, issues: [] }</c></item>
    /// <item><b>Invalid:</b> <c>{ isValid: false, errorCount: N, warningCount: M, issues: [...] }</c></item>
    /// <item><b>Fetch Error:</b> <c>{ success: false, errorMessage: "..." }</c></item>
    /// </list>
    /// </returns>
    [McpServerTool(Name = "llmstxt_validate")]
    [Description(
        "Runs spec-compliance validation on a domain's /llms.txt file and returns a structured " +
        "report. Use this to assess the quality and trustworthiness of a site's llms.txt before " +
        "relying on its content. Returns isValid (true if zero errors), counts of errors and " +
        "warnings, and detailed issue descriptions with severity, rule ID, and location. " +
        "Optionally enable check_urls to verify linked resources are reachable, and " +
        "check_freshness to detect stale content.")]
    public static async Task<string> Validate(
        [Description("The domain to validate (e.g., 'docs.anthropic.com'). " +
                     "Do not include 'https://' or '/llms.txt'.")]
        string domain,
        [Description("Whether to verify linked URLs resolve (HTTP HEAD checks). " +
                     "Default: false. Slower but catches broken links.")]
        bool checkUrls = false,
        [Description("Whether to compare Last-Modified headers for staleness detection. " +
                     "Default: false. Adds network overhead.")]
        bool checkFreshness = false,
        LlmsTxtFetcher? fetcher = null,
        LlmsTxtCache? cache = null,
        LlmsDocumentValidator? validator = null,
        ILogger<ValidateTool>? logger = null)
    {
        // Use null-forgiving for DI-injected services — MCP SDK guarantees
        // these are provided at runtime, but the nullable signature allows
        // direct construction in tests.
        var log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ValidateTool>.Instance;

        // ---------------------------------------------------------------
        // Input validation
        // ---------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(domain))
        {
            log.LogWarning("llmstxt_validate called with empty domain parameter");
            return SerializeResponse(new ValidateErrorResponse
            {
                Success = false,
                ErrorMessage = "The 'domain' parameter is required and cannot be empty."
            });
        }

        log.LogInformation(
            "llmstxt_validate: Validating domain '{Domain}' (checkUrls={CheckUrls}, checkFreshness={CheckFreshness})",
            domain, checkUrls, checkFreshness);

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
        // Run validation
        // ---------------------------------------------------------------
        var validationOptions = new ValidationOptions
        {
            CheckLinkedUrls = checkUrls,
            CheckFreshness = checkFreshness
        };

        log.LogDebug(
            "llmstxt_validate: Running validation on '{Domain}' document " +
            "(title='{Title}', {SectionCount} sections, checkUrls={CheckUrls}, checkFreshness={CheckFreshness})",
            domain, document.Title, document.Sections.Count, checkUrls, checkFreshness);

        ValidationReport report;
        try
        {
            report = await validator!.ValidateAsync(document, validationOptions);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "llmstxt_validate: Unexpected exception during validation for '{Domain}'", domain);
            return SerializeResponse(new ValidateErrorResponse
            {
                Success = false,
                ErrorMessage = $"Unexpected error during validation: {ex.Message}"
            });
        }

        // ---------------------------------------------------------------
        // Build the validation response
        // ---------------------------------------------------------------
        var issues = report.AllIssues.Select(issue => new ValidateIssue
        {
            Severity = issue.Severity.ToString().ToLowerInvariant(),
            Rule = issue.Rule,
            Message = issue.Message,
            Location = issue.Location
        }).ToList();

        log.LogInformation(
            "llmstxt_validate: Validation complete for '{Domain}' — " +
            "IsValid={IsValid}, Errors={ErrorCount}, Warnings={WarningCount}, fromCache={FromCache}",
            domain, report.IsValid, report.Errors.Count, report.Warnings.Count, fromCache);

        if (!report.IsValid)
        {
            log.LogDebug(
                "llmstxt_validate: Error details for '{Domain}': {Errors}",
                domain, string.Join("; ", report.Errors.Select(e => $"[{e.Rule}] {e.Message}")));
        }

        if (report.Warnings.Count > 0)
        {
            log.LogDebug(
                "llmstxt_validate: Warning details for '{Domain}': {Warnings}",
                domain, string.Join("; ", report.Warnings.Select(w => $"[{w.Rule}] {w.Message}")));
        }

        var response = new ValidateSuccessResponse
        {
            IsValid = report.IsValid,
            ErrorCount = report.Errors.Count,
            WarningCount = report.Warnings.Count,
            Issues = issues,
            Domain = domain,
            Title = document.Title,
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
                "llmstxt_validate: Cache hit for '{Domain}' (fetched at {FetchedAt})",
                domain, cached.FetchedAt);
            return (cached.Document, true, null);
        }

        // Fetch from origin
        FetchResult result;
        try
        {
            logger.LogDebug("llmstxt_validate: Fetching https://{Domain}/llms.txt", domain);
            result = await fetcher.FetchAsync(domain);
            logger.LogDebug(
                "llmstxt_validate: Fetch completed for '{Domain}' — Status={Status}",
                domain, result.Status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "llmstxt_validate: Unexpected exception fetching '{Domain}'", domain);
            var errorResp = SerializeResponse(new ValidateErrorResponse
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
                "llmstxt_validate: Cannot validate — {Status} for '{Domain}': {Message}",
                result.Status, domain, statusMessage);

            var errorResp = SerializeResponse(new ValidateErrorResponse
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
            "llmstxt_validate: Cached document for '{Domain}' — Title='{Title}'",
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
    /// JSON response for a successful validation run (regardless of whether
    /// the document is valid or invalid — "success" means validation itself
    /// completed without errors).
    /// </summary>
    internal sealed class ValidateSuccessResponse
    {
        /// <summary>Whether the document has zero error-severity issues.</summary>
        public bool IsValid { get; set; }

        /// <summary>The count of error-severity issues.</summary>
        public int ErrorCount { get; set; }

        /// <summary>The count of warning-severity issues.</summary>
        public int WarningCount { get; set; }

        /// <summary>All validation issues found, sorted by severity.</summary>
        public List<ValidateIssue> Issues { get; set; } = new();

        /// <summary>The domain that was validated.</summary>
        public string Domain { get; set; } = string.Empty;

        /// <summary>The document title (for confirmation).</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Whether the llms.txt document was served from cache.</summary>
        public bool FromCache { get; set; }
    }

    /// <summary>
    /// JSON response when the tool cannot run validation (domain unreachable, etc.).
    /// </summary>
    internal sealed class ValidateErrorResponse
    {
        /// <summary>Always false — the validation could not be performed.</summary>
        public bool Success { get; set; }

        /// <summary>The fetch status code, if the failure was during llms.txt fetch.</summary>
        public string? Status { get; set; }

        /// <summary>Human-readable error description.</summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// A single validation issue in the response.
    /// </summary>
    internal sealed class ValidateIssue
    {
        /// <summary>Issue severity: "error" or "warning".</summary>
        public string Severity { get; set; } = string.Empty;

        /// <summary>Machine-readable rule identifier (e.g., "REQUIRED_H1_MISSING").</summary>
        public string Rule { get; set; } = string.Empty;

        /// <summary>Human-readable description of the issue.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Where in the document the issue was found, or null if not localized.</summary>
        public string? Location { get; set; }
    }
}
