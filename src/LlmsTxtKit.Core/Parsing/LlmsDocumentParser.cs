using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LlmsTxtKit.Core.Parsing;

/// <summary>
/// Parses raw llms.txt content into a strongly-typed <see cref="LlmsDocument"/>.
/// </summary>
/// <remarks>
/// <para>
/// The parser is behaviorally aligned with the reference Python implementation
/// (<c>miniparse.py</c> from <c>AnswerDotAI/llms-txt</c>). Key behavioral details:
/// </para>
/// <list type="bullet">
/// <item>Sections are delimited by H2 headings only (<c>^##\s+</c>). H3+ headings
/// are treated as content within the parent section.</item>
/// <item>The blockquote summary captures a single line (<c>^&gt;\s*(.+)$</c>).</item>
/// <item>The "Optional" section check is case-sensitive and exact-match.</item>
/// <item>Link entries follow the pattern:
/// <c>-\s*\[title\](url)(?::\s*description)?</c>.</item>
/// <item>Freeform content between the blockquote and first H2 is captured as a
/// single blob (the "info" field in the reference implementation).</item>
/// </list>
/// <para>
/// The parser never throws on malformed input. Instead, it returns an
/// <see cref="LlmsDocument"/> with whatever it could extract, plus
/// <see cref="ParseDiagnostic"/> entries for any issues encountered.
/// Only programming errors (null input) throw exceptions.
/// </para>
/// </remarks>
public static class LlmsDocumentParser
{
    // -- Compiled regex patterns (static, thread-safe) --

    /// <summary>H1 heading: <c># Title text</c>.</summary>
    private static readonly Regex H1Pattern = new(
        @"^#\s+(.+)$",
        RegexOptions.Compiled);

    /// <summary>H2 heading: <c>## Section name</c>.</summary>
    private static readonly Regex H2Pattern = new(
        @"^##\s+(.+)$",
        RegexOptions.Compiled);

    /// <summary>Blockquote line: <c>&gt; Summary text</c>.</summary>
    private static readonly Regex BlockquotePattern = new(
        @"^>\s*(.+)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Link entry: <c>- [Title](url)</c> or <c>- [Title](url): Description</c>.
    /// Aligned with the reference parser's canonical link pattern.
    /// </summary>
    private static readonly Regex EntryPattern = new(
        @"^-\s*\[([^\]]+)\]\(([^\)]+)\)(?::\s*(.*))?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses the provided raw llms.txt content string.
    /// </summary>
    /// <param name="content">The raw Markdown content of an llms.txt file.</param>
    /// <param name="logger">
    /// Optional logger for surfacing parse diagnostics as log entries. When provided,
    /// warnings and errors discovered during parsing are logged in addition to being
    /// included in the returned <see cref="LlmsDocument.Diagnostics"/> collection.
    /// </param>
    /// <returns>
    /// A parsed <see cref="LlmsDocument"/> containing the extracted structure and
    /// any <see cref="ParseDiagnostic"/> entries for issues encountered during parsing.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="content"/> is <c>null</c>.
    /// Null input is a programming error, not a runtime condition.
    /// </exception>
    public static LlmsDocument Parse(string content, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        var diagnostics = new List<ParseDiagnostic>();
        var lines = content.Split('\n');

        string? title = null;
        int titleLine = -1;
        string? summary = null;
        var freeformLines = new List<string>();
        var sections = new List<LlmsSection>();

        // Parsing state machine
        bool foundTitle = false;
        bool foundSummary = false;
        bool inFreeform = false;      // Between summary/title and first H2
        string? currentSectionName = null;
        var currentEntries = new List<LlmsEntry>();
        bool inSection = false;

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNumber = i + 1; // 1-based
            string line = lines[i].TrimEnd('\r'); // Handle \r\n line endings

            // --- Check for H1 ---
            var h1Match = H1Pattern.Match(line);
            if (h1Match.Success)
            {
                if (foundTitle)
                {
                    diagnostics.Add(new ParseDiagnostic(
                        DiagnosticSeverity.Error,
                        "Multiple H1 headings found. The llms.txt spec requires exactly one H1 title.",
                        lineNumber));
                    continue;
                }

                title = h1Match.Groups[1].Value.Trim();
                titleLine = lineNumber;
                foundTitle = true;
                continue;
            }

            // --- Check for blockquote (only capture the first one, before any H2) ---
            if (!foundSummary && !inSection)
            {
                var bqMatch = BlockquotePattern.Match(line);
                if (bqMatch.Success && foundTitle)
                {
                    summary = bqMatch.Groups[1].Value.Trim();
                    foundSummary = true;
                    inFreeform = true; // Start watching for freeform content
                    continue;
                }
            }

            // --- Check for H2 (section delimiter) ---
            var h2Match = H2Pattern.Match(line);
            if (h2Match.Success)
            {
                // If we were collecting freeform content, stop
                if (inFreeform)
                {
                    inFreeform = false;
                }

                // If we were in a section, finalize it
                if (inSection && currentSectionName is not null)
                {
                    sections.Add(new LlmsSection(
                        currentSectionName,
                        currentSectionName == "Optional",
                        currentEntries.AsReadOnly()));
                    currentEntries = new List<LlmsEntry>();
                }

                currentSectionName = h2Match.Groups[1].Value.Trim();
                inSection = true;
                continue;
            }

            // --- If we're in a section, try to parse entry links ---
            if (inSection)
            {
                // Skip blank lines within sections
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var entryMatch = EntryPattern.Match(line);
                if (entryMatch.Success)
                {
                    string entryTitle = entryMatch.Groups[1].Value.Trim();
                    string urlString = entryMatch.Groups[2].Value.Trim();
                    string? description = entryMatch.Groups[3].Success
                        ? entryMatch.Groups[3].Value.Trim()
                        : null;

                    // Normalize empty descriptions to null
                    if (string.IsNullOrWhiteSpace(description))
                        description = null;

                    if (Uri.TryCreate(urlString, UriKind.Absolute, out var uri))
                    {
                        currentEntries.Add(new LlmsEntry(uri, entryTitle, description));
                    }
                    else
                    {
                        diagnostics.Add(new ParseDiagnostic(
                            DiagnosticSeverity.Warning,
                            $"Entry URL is not a valid absolute URI: \"{urlString}\".",
                            lineNumber));
                    }
                }
                // Non-link, non-blank lines within a section are ignored
                // (H3+ headings, plain text, etc. are treated as section content)
                continue;
            }

            // --- Freeform content collection ---
            if (inFreeform && !string.IsNullOrWhiteSpace(line))
            {
                freeformLines.Add(line);
                continue;
            }

            // If we found the title but no summary yet, and this is a
            // non-blank, non-heading, non-blockquote line — start freeform
            if (foundTitle && !foundSummary && !inSection && !string.IsNullOrWhiteSpace(line))
            {
                inFreeform = true;
                freeformLines.Add(line);
                continue;
            }

            // Capture blank lines within freeform content blocks
            if (inFreeform)
            {
                freeformLines.Add(line);
            }
        }

        // Finalize the last section if we were in one
        if (inSection && currentSectionName is not null)
        {
            sections.Add(new LlmsSection(
                currentSectionName,
                currentSectionName == "Optional",
                currentEntries.AsReadOnly()));
        }

        // Validate required elements
        if (!foundTitle)
        {
            diagnostics.Add(new ParseDiagnostic(
                DiagnosticSeverity.Error,
                "No H1 title found. The llms.txt spec requires an H1 heading as the document title."));
        }

        // Build freeform content
        string? freeformContent = freeformLines.Count > 0
            ? string.Join("\n", freeformLines).Trim()
            : null;

        // Normalize empty freeform to null
        if (string.IsNullOrWhiteSpace(freeformContent))
            freeformContent = null;

        // Surface diagnostics to logger if available
        if (logger != null && diagnostics.Count > 0)
        {
            foreach (var diag in diagnostics)
            {
                if (diag.Severity == DiagnosticSeverity.Error)
                    logger.LogWarning("Parse error at line {Line}: {Message}", diag.Line, diag.Message);
                else
                    logger.LogDebug("Parse warning at line {Line}: {Message}", diag.Line, diag.Message);
            }
        }

        logger?.LogDebug("Parse complete: title={Title}, sections={SectionCount}, entries={EntryCount}, diagnostics={DiagCount}",
            title ?? "(none)", sections.Count,
            sections.Sum(s => s.Entries.Count),
            diagnostics.Count);

        return new LlmsDocument(
            Title: title ?? string.Empty,
            Summary: summary,
            FreeformContent: freeformContent,
            Sections: sections.AsReadOnly(),
            Diagnostics: diagnostics.AsReadOnly(),
            RawContent: content);
    }
}
