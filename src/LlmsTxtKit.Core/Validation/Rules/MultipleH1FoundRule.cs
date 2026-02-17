using LlmsTxtKit.Core.Parsing;

namespace LlmsTxtKit.Core.Validation.Rules;

/// <summary>
/// Rule: <c>MULTIPLE_H1_FOUND</c> — Checks that the parser did not detect
/// multiple H1 headings (captured via <see cref="ParseDiagnostic"/>).
/// Severity: <see cref="ValidationSeverity.Error"/>.
/// </summary>
/// <remarks>
/// The llms.txt spec requires exactly one H1 heading. When the parser encounters
/// additional H1 headings, it records an error diagnostic. This rule bridges the
/// parser's diagnostics into the validation report.
/// </remarks>
public sealed class MultipleH1FoundRule : IValidationRule
{
    /// <inheritdoc/>
    public Task<IEnumerable<ValidationIssue>> EvaluateAsync(
        LlmsDocument document, ValidationOptions options, CancellationToken cancellationToken = default)
    {
        var issues = new List<ValidationIssue>();

        // The parser records "Multiple H1 headings" as an Error diagnostic
        var multipleH1Diagnostics = document.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error
                        && d.Message.Contains("Multiple H1", StringComparison.OrdinalIgnoreCase));

        foreach (var diag in multipleH1Diagnostics)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "MULTIPLE_H1_FOUND",
                "More than one H1 heading found. The llms.txt spec requires exactly one H1 title.",
                diag.Line.HasValue ? $"Line {diag.Line}" : null));
        }

        return Task.FromResult<IEnumerable<ValidationIssue>>(issues);
    }
}
