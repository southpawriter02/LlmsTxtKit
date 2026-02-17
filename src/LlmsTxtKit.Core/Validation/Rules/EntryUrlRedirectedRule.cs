using LlmsTxtKit.Core.Parsing;

namespace LlmsTxtKit.Core.Validation.Rules;

/// <summary>
/// Rule: <c>ENTRY_URL_REDIRECTED</c> — Checks whether linked URLs return 3xx
/// redirect responses, which may indicate stale references that should be
/// updated to the final destination URL.
/// Severity: <see cref="ValidationSeverity.Warning"/>.
/// </summary>
/// <remarks>
/// Only runs when <see cref="ValidationOptions.CheckLinkedUrls"/> is <c>true</c>.
/// Uses <see cref="HttpClientHandler.AllowAutoRedirect"/> = <c>false</c> to
/// detect redirects before they're followed.
/// </remarks>
public sealed class EntryUrlRedirectedRule : IValidationRule
{
    private readonly HttpClient? _httpClient;

    /// <summary>
    /// Creates a new instance with an optional externally-managed HTTP client.
    /// The supplied client should have <c>AllowAutoRedirect = false</c> to
    /// detect redirects.
    /// </summary>
    public EntryUrlRedirectedRule(HttpClient? httpClient = null)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<ValidationIssue>> EvaluateAsync(
        LlmsDocument document, ValidationOptions options, CancellationToken cancellationToken = default)
    {
        if (!options.CheckLinkedUrls)
            return [];

        var issues = new List<ValidationIssue>();

        // Create a client that does NOT follow redirects so we can detect them
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = _httpClient ?? new HttpClient(handler);

        try
        {
            foreach (var section in document.Sections)
            {
                foreach (var entry in section.Entries)
                {
                    if (entry.Url.Scheme is not ("http" or "https"))
                        continue;

                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(TimeSpan.FromSeconds(options.UrlCheckTimeout));

                        using var request = new HttpRequestMessage(HttpMethod.Head, entry.Url);
                        using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);

                        int statusCode = (int)response.StatusCode;
                        if (statusCode is >= 300 and < 400)
                        {
                            var location = response.Headers.Location?.ToString() ?? "(unknown)";
                            issues.Add(new ValidationIssue(
                                ValidationSeverity.Warning,
                                "ENTRY_URL_REDIRECTED",
                                $"Linked URL \"{entry.Url}\" redirects (HTTP {statusCode}) "
                                + $"to \"{location}\". Consider updating the URL to the final destination.",
                                $"Section: {section.Name}"));
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                    {
                        // Connectivity failures are handled by EntryUrlUnreachableRule;
                        // this rule only cares about redirect detection.
                    }
                }
            }
        }
        finally
        {
            if (_httpClient is null)
            {
                client.Dispose();
                handler.Dispose();
            }
        }

        return issues;
    }
}
