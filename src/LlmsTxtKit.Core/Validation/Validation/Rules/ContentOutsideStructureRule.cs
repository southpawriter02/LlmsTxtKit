using System.Text.RegularExpressions;
using LlmsTxtKit.Core.Parsing;

namespace LlmsTxtKit.Core.Validation.Rules;

/// <summary>
/// Rule: <c>CONTENT_OUTSIDE_STRUCTURE</c> — Detects content that doesn't belong
/// to any defined structural element (orphaned text between sections, content
/// after the last section that isn't a valid entry, etc.).
/// Severity: <see cref="ValidationSeverity.Warning"/>.
/// </summary>
/// <remarks>
/// <para>
/// The llms.txt spec defines a strict structure: H1 title, optional blockquote,
/// optional freeform content, then H2 sections each containing link entries.
/// Content that falls outside these elements may indicate authoring mistakes
/// such as misformatted links or misplaced paragraphs.
/// </para>
/// <para>
/// This rule performs a lightweight heuristic check by scanning for non-blank
/// lines within sections that are not valid entry links and not H3+ headings
/// (which are already flagged by <see cref="UnexpectedHeadingLevelRule"/>).
/// This identifies plain text "orphans" that the parser silently skips.
/// </para>
/// </remarks>
public sealed class ContentOutsideStructureRule : IValidationRule
{
    /// <summary>H2 heading pattern.</summary>
    private static readonly Regex H2Pattern = new(@"^##\s+", RegexOptions.Compiled);

    /// <summary>H1 heading pattern.</summary>
    private static readonly Regex H1Pattern = new(@"^#\s+", RegexOptions.Compiled);

    /// <summary>Blockquote pattern.</summary>
    private static readonly Regex BlockquotePattern = new(@"^>\s*", RegexOptions.Compiled);

    /// <summary>Link entry pattern matching the parser's canonical format.</summary>
    private static readonly Regex EntryPattern = new(
        @"^-\s*\[([^\]]+)\]\(([^\)]+)\)",
        RegexOptions.Compiled);

    /// <summary>H3+ heading pattern.</summary>
    private static readonly Regex H3PlusPattern = new(@"^#{3,6}\s+", RegexOptions.Compiled);

    /// <inheritdoc/>
    public Task<IEnumerable<ValidationIssue>> EvaluateAsync(
        LlmsDocument document, ValidationOptions options, CancellationToken cancellationToken = default)
    {
        var issues = new List<ValidationIssue>();

        if (string.IsNullOrEmpty(document.RawContent))
            return Task.FromResult<IEnumerable<ValidationIssue>>(issues);

        var lines = document.RawContent.Split('\n');
        bool inSection = false;
        bool pastTitle = false;
        bool pastSummary = false;
        bool inFreeform = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            // Skip blank lines everywhere
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Track structural context
            if (H1Pattern.IsMatch(line))
            {
                pastTitle = true;
                continue;
            }

            if (!pastSummary && BlockquotePattern.IsMatch(line) && pastTitle)
            {
                pastSummary = true;
                inFreeform = true;
                continue;
            }

            if (H2Pattern.IsMatch(line))
            {
                inSection = true;
                inFreeform = false;
                continue;
            }

            // Freeform content between summary and first H2 is valid
            if (inFreeform && !inSection)
                continue;

            // Content before the title or between title and first H2 (no summary)
            if (!inSection && pastTitle && !pastSummary)
            {
                inFreeform = true;
                continue;
            }

            // Inside a section: entries and H3+ headings are expected (H3+ flagged elsewhere)
            if (inSection)
            {
                if (EntryPattern.IsMatch(line) || H3PlusPattern.IsMatch(line))
                    continue;

                // Non-entry, non-heading content inside a section is orphaned
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    "CONTENT_OUTSIDE_STRUCTURE",
                    $"Line \"{Truncate(line, 60)}\" appears inside a section but is not a "
                    + "valid entry link. It may be a misformatted link or misplaced text.",
                    $"Line {i + 1}"));
            }
        }

        return Task.FromResult<IEnumerable<ValidationIssue>>(issues);
    }

    /// <summary>
    /// Truncates a string to a maximum length, appending "..." if truncated.
    /// </summary>
    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;
        return value[..(maxLength - 3)] + "...";
    }
}
