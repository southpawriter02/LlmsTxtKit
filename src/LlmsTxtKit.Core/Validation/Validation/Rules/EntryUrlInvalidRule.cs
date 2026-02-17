using LlmsTxtKit.Core.Parsing;

namespace LlmsTxtKit.Core.Validation.Rules;

/// <summary>
/// Rule: <c>ENTRY_URL_INVALID</c> — Checks that all entry URLs are valid
/// absolute HTTP or HTTPS URIs. Per SEC-4, relative URLs, <c>javascript:</c>,
/// <c>data:</c>, and other non-HTTP(S) schemes are rejected.
/// Severity: <see cref="ValidationSeverity.Error"/>.
/// </summary>
public sealed class EntryUrlInvalidRule : IValidationRule
{
    /// <inheritdoc/>
    public Task<IEnumerable<ValidationIssue>> EvaluateAsync(
        LlmsDocument document, ValidationOptions options, CancellationToken cancellationToken = default)
    {
        var issues = new List<ValidationIssue>();

        foreach (var section in document.Sections)
        {
            foreach (var entry in section.Entries)
            {
                // The parser already validates that URLs parse as absolute URIs,
                // but we additionally enforce that the scheme is HTTP or HTTPS.
                if (entry.Url.Scheme is not ("http" or "https"))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "ENTRY_URL_INVALID",
                        $"Entry URL \"{entry.Url}\" uses scheme \"{entry.Url.Scheme}\". "
                        + "Only http:// and https:// URLs are permitted.",
                        $"Section: {section.Name}"));
                }
            }
        }

        // Also pick up URL parse failures from parser diagnostics
        // (entries with invalid URLs won't appear in section.Entries because
        // the parser filters them out, but the diagnostics record the issue)
        var urlDiagnostics = document.Diagnostics
            .Where(d => d.Message.Contains("URL", StringComparison.OrdinalIgnoreCase)
                        && d.Message.Contains("valid", StringComparison.OrdinalIgnoreCase));

        foreach (var diag in urlDiagnostics)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "ENTRY_URL_INVALID",
                $"An entry contains an invalid URL: {diag.Message}",
                diag.Line.HasValue ? $"Line {diag.Line}" : null));
        }

        return Task.FromResult<IEnumerable<ValidationIssue>>(issues);
    }
}
