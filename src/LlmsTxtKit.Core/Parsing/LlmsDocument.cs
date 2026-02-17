namespace LlmsTxtKit.Core.Parsing;

/// <summary>
/// Represents a fully parsed llms.txt document. This is the primary output
/// of <see cref="LlmsDocumentParser.Parse(string)"/>.
/// </summary>
/// <remarks>
/// <para>
/// The document model is immutable once constructed. The parser produces it;
/// consumers read it. This makes instances safe for caching and concurrent access.
/// </para>
/// <para>
/// The parser uses lenient parsing: it extracts whatever structure it can from
/// the input and records any issues as <see cref="ParseDiagnostic"/> entries
/// in the <see cref="Diagnostics"/> collection. A document with diagnostics
/// is still usable — callers decide their own strictness threshold.
/// </para>
/// </remarks>
/// <param name="Title">
/// The H1 heading content. Empty string if no H1 was found (an error diagnostic
/// will be present in <see cref="Diagnostics"/>).
/// </param>
/// <param name="Summary">
/// The blockquote content immediately following the H1, or <c>null</c> if
/// no blockquote was present.
/// </param>
/// <param name="FreeformContent">
/// Markdown content between the summary (or title, if no summary) and the
/// first H2 section, or <c>null</c> if no such content exists. This corresponds
/// to the "info" field in the reference Python implementation.
/// </param>
/// <param name="Sections">The ordered list of H2-delimited sections.</param>
/// <param name="Diagnostics">
/// Parse diagnostics (warnings and errors) collected during parsing.
/// An empty list indicates a clean parse with no issues detected.
/// </param>
/// <param name="RawContent">
/// The original, unparsed input string. Preserved for debugging and
/// round-trip verification.
/// </param>
public sealed record LlmsDocument(
    string Title,
    string? Summary,
    string? FreeformContent,
    IReadOnlyList<LlmsSection> Sections,
    IReadOnlyList<ParseDiagnostic> Diagnostics,
    string RawContent);
