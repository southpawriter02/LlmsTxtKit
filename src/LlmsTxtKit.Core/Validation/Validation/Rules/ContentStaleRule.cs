using LlmsTxtKit.Core.Parsing;

namespace LlmsTxtKit.Core.Validation.Rules;

/// <summary>
/// Rule: <c>CONTENT_STALE</c> — Compares the <c>Last-Modified</c> header of the
/// llms.txt file against the <c>Last-Modified</c> headers of linked pages. If the
/// llms.txt file is older, the content may be stale.
/// Severity: <see cref="ValidationSeverity.Warning"/>.
/// </summary>
/// <remarks>
/// Only runs when <see cref="ValidationOptions.CheckFreshness"/> is <c>true</c>.
/// Requires the llms.txt file's <c>Last-Modified</c> timestamp to be provided
/// via <see cref="LlmsTxtLastModified"/>. If not available, the rule is skipped.
/// </remarks>
public sealed class ContentStaleRule : IValidationRule
{
    private readonly HttpClient? _httpClient;

    /// <summary>
    /// The <c>Last-Modified</c> timestamp of the llms.txt file itself.
    /// Must be set before evaluation for this rule to produce results.
    /// </summary>
    public DateTimeOffset? LlmsTxtLastModified { get; set; }

    /// <summary>
    /// Creates a new instance with an optional externally-managed HTTP client.
    /// </summary>
    public ContentStaleRule(HttpClient? httpClient = null)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<ValidationIssue>> EvaluateAsync(
        LlmsDocument document, ValidationOptions options, CancellationToken cancellationToken = default)
    {
        if (!options.CheckFreshness || !LlmsTxtLastModified.HasValue)
            return [];

        var issues = new List<ValidationIssue>();
        var client = _httpClient ?? new HttpClient();

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

                        var linkedLastModified = response.Content.Headers.LastModified;
                        if (linkedLastModified.HasValue
                            && linkedLastModified.Value > LlmsTxtLastModified.Value)
                        {
                            issues.Add(new ValidationIssue(
                                ValidationSeverity.Warning,
                                "CONTENT_STALE",
                                $"Linked URL \"{entry.Url}\" was modified on "
                                + $"{linkedLastModified.Value:yyyy-MM-dd}, which is newer than the "
                                + $"llms.txt file ({LlmsTxtLastModified.Value:yyyy-MM-dd}). "
                                + "The llms.txt file may need updating.",
                                $"Section: {section.Name}"));
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                    {
                        // Network failures are handled by other rules; skip silently.
                    }
                }
            }
        }
        finally
        {
            if (_httpClient is null)
                client.Dispose();
        }

        return issues;
    }
}
