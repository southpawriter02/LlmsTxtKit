namespace LlmsTxtKit.Core.Parsing;

/// <summary>
/// Indicates the severity of a parse diagnostic.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>
    /// A non-fatal issue. The document was parsed successfully but contains
    /// content that may not conform to the llms.txt specification.
    /// </summary>
    Warning,

    /// <summary>
    /// A structural problem that indicates the document is invalid
    /// per the llms.txt specification (e.g., missing required H1 title).
    /// </summary>
    Error
}

/// <summary>
/// Represents a single diagnostic message produced during parsing.
/// The parser never throws on malformed input; instead, it collects
/// diagnostics that callers can inspect to determine document quality.
/// </summary>
/// <param name="Severity">The severity level of this diagnostic.</param>
/// <param name="Message">A human-readable description of the issue.</param>
/// <param name="Line">
/// The 1-based line number where the issue was detected, or <c>null</c>
/// if the issue is not localized to a specific line.
/// </param>
public sealed record ParseDiagnostic(
    DiagnosticSeverity Severity,
    string Message,
    int? Line = null);
