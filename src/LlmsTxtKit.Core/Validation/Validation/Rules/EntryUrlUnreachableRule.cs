using System.Net;
using LlmsTxtKit.Core.Parsing;

namespace LlmsTxtKit.Core.Validation.Rules;

/// <summary>
/// Rule: <c>ENTRY_URL_UNREACHABLE</c> — Sends HTTP HEAD requests to each linked
/// URL and reports any that return non-200 responses.
/// Severity: <see cref="ValidationSeverity.Warning"/>.
/// </summary>
/// <remarks>
/// <para>
/// This rule only runs when <see cref="ValidationOptions.CheckLinkedUrls"/> is
/// <c>true</c>. It is disabled by default because it is slow (one HTTP request
/// per entry) and may produce false positives due to transient network issues.
/// </para>
/// <para>
/// The rule uses HTTP HEAD (not GET) to minimize bandwidth usage. Each request
/// has an independent timeout controlled by <see cref="ValidationOptions.UrlCheckTimeout"/>.
/// </para>
/// </remarks>
public sealed class EntryUrlUnreachableRule : IValidationRule
{
    private readonly HttpClient? _httpClient;

    /// <summary>
    /// Creates a new instance with an optional externally-managed HTTP client.
    /// </summary>
    /// <param name="httpClient">
    /// Optional <see cref="HttpClient"/> for URL checks. If <c>null</c>,
    /// a default client is created per invocation.
    /// </param>
    public EntryUrlUnreachableRule(HttpClient? httpClient = null)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<ValidationIssue>> EvaluateAsync(
        LlmsDocument document, ValidationOptions options, CancellationToken cancellationToken = default)
    {
        // Only run if URL checking is enabled
        if (!options.CheckLinkedUrls)
            return [];

        var issues = new List<ValidationIssue>();
        var client = _httpClient ?? new HttpClient();

        try
        {
            foreach (var section in document.Sections)
            {
                foreach (var entry in section.Entries)
                {
                    // Only check HTTP/HTTPS URLs
                    if (entry.Url.Scheme is not ("http" or "https"))
                        continue;

                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(TimeSpan.FromSeconds(options.UrlCheckTimeout));

                        using var request = new HttpRequestMessage(HttpMethod.Head, entry.Url);
                        using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);

                        if (!response.IsSuccessStatusCode)
                        {
                            issues.Add(new ValidationIssue(
                                ValidationSeverity.Warning,
                                "ENTRY_URL_UNREACHABLE",
                                $"Linked URL \"{entry.Url}\" returned HTTP {(int)response.StatusCode} "
                                + $"({response.StatusCode}).",
                                $"Section: {section.Name}"));
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                    {
                        issues.Add(new ValidationIssue(
                            ValidationSeverity.Warning,
                            "ENTRY_URL_UNREACHABLE",
                            $"Linked URL \"{entry.Url}\" is unreachable: {ex.Message}",
                            $"Section: {section.Name}"));
                    }
                }
            }
        }
        finally
        {
            // Only dispose if we created the client ourselves
            if (_httpClient is null)
                client.Dispose();
        }

        return issues;
    }
}
