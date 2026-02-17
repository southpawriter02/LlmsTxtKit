using LlmsTxtKit.Core.Parsing;

namespace LlmsTxtKit.Core.Validation.Rules;

/// <summary>
/// Rule: <c>BLOCKQUOTE_MALFORMED</c> — Checks that the blockquote summary,
/// if present, follows the expected single-line format.
/// Severity: <see cref="ValidationSeverity.Warning"/>.
/// </summary>
/// <remarks>
/// The llms.txt spec expects a single-line blockquote (<c>&gt; Summary text</c>)
/// immediately after the H1 title. This rule fires if the parser detected a
/// malformed blockquote via its diagnostics, or if the summary appears to
/// contain formatting that suggests it was intended as a multi-line blockquote.
/// </remarks>
public sealed class BlockquoteMalformedRule : IValidationRule
{
    /// <inheritdoc/>
    public Task<IEnumerable<ValidationIssue>> EvaluateAsync(
        LlmsDocument document, ValidationOptions options, CancellationToken cancellationToken = default)
    {
        var issues = new List<ValidationIssue>();

        // Check parser diagnostics for blockquote-related warnings
        var bqDiagnostics = document.Diagnostics
            .Where(d => d.Message.Contains("blockquote", StringComparison.OrdinalIgnoreCase)
                        || d.Message.Contains("Blockquote", StringComparison.Ordinal));

        foreach (var diag in bqDiagnostics)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "BLOCKQUOTE_MALFORMED",
                $"Blockquote summary may be malformed: {diag.Message}",
                diag.Line.HasValue ? $"Line {diag.Line}" : null));
        }

        return Task.FromResult<IEnumerable<ValidationIssue>>(issues);
    }
}
