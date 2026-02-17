using LlmsTxtKit.Core.Parsing;

namespace LlmsTxtKit.Core.Validation.Rules;

/// <summary>
/// Rule: <c>SECTION_EMPTY</c> — Checks that each H2 section contains at least
/// one entry. Empty sections may indicate incomplete or placeholder content.
/// Severity: <see cref="ValidationSeverity.Warning"/>.
/// </summary>
public sealed class SectionEmptyRule : IValidationRule
{
    /// <inheritdoc/>
    public Task<IEnumerable<ValidationIssue>> EvaluateAsync(
        LlmsDocument document, ValidationOptions options, CancellationToken cancellationToken = default)
    {
        var issues = new List<ValidationIssue>();

        foreach (var section in document.Sections)
        {
            if (section.Entries.Count == 0)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    "SECTION_EMPTY",
                    $"Section \"{section.Name}\" has no entries. Empty sections may indicate incomplete content.",
                    $"Section: {section.Name}"));
            }
        }

        return Task.FromResult<IEnumerable<ValidationIssue>>(issues);
    }
}
