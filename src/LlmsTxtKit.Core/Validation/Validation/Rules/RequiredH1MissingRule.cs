using LlmsTxtKit.Core.Parsing;

namespace LlmsTxtKit.Core.Validation.Rules;

/// <summary>
/// Rule: <c>REQUIRED_H1_MISSING</c> — Checks that the document has a non-empty H1 title.
/// Severity: <see cref="ValidationSeverity.Error"/>.
/// </summary>
/// <remarks>
/// The llms.txt spec requires exactly one H1 heading as the document title.
/// A document with no title (empty string) is structurally invalid.
/// </remarks>
public sealed class RequiredH1MissingRule : IValidationRule
{
    /// <inheritdoc/>
    public Task<IEnumerable<ValidationIssue>> EvaluateAsync(
        LlmsDocument document, ValidationOptions options, CancellationToken cancellationToken = default)
    {
        var issues = new List<ValidationIssue>();

        if (string.IsNullOrWhiteSpace(document.Title))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "REQUIRED_H1_MISSING",
                "No H1 title found. The llms.txt spec requires exactly one H1 heading as the document title."));
        }

        return Task.FromResult<IEnumerable<ValidationIssue>>(issues);
    }
}
