using System.Text.RegularExpressions;
using LlmsTxtKit.Core.Parsing;

namespace LlmsTxtKit.Core.Validation.Rules;

/// <summary>
/// Rule: <c>UNEXPECTED_HEADING_LEVEL</c> — Checks for headings other than H1
/// and H2 at the structural level of the document. The llms.txt spec only
/// defines H1 (title) and H2 (sections); H3+ headings are unexpected.
/// Severity: <see cref="ValidationSeverity.Warning"/>.
/// </summary>
/// <remarks>
/// H3+ headings within section content are not necessarily wrong (they might
/// be part of freeform Markdown), but their presence at the structural level
/// suggests the author may have intended them as sections, which the spec
/// does not support.
/// </remarks>
public sealed class UnexpectedHeadingLevelRule : IValidationRule
{
    /// <summary>
    /// Regex matching H3 through H6 headings at the start of a line.
    /// </summary>
    private static readonly Regex H3PlusPattern = new(
        @"^#{3,6}\s+",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <inheritdoc/>
    public Task<IEnumerable<ValidationIssue>> EvaluateAsync(
        LlmsDocument document, ValidationOptions options, CancellationToken cancellationToken = default)
    {
        var issues = new List<ValidationIssue>();

        // Scan the raw content for H3+ headings
        if (!string.IsNullOrEmpty(document.RawContent))
        {
            var lines = document.RawContent.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (H3PlusPattern.IsMatch(line))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Warning,
                        "UNEXPECTED_HEADING_LEVEL",
                        $"Heading \"{line.Trim()}\" uses a level deeper than H2. "
                        + "The llms.txt spec only defines H1 (title) and H2 (sections).",
                        $"Line {i + 1}"));
                }
            }
        }

        return Task.FromResult<IEnumerable<ValidationIssue>>(issues);
    }
}
