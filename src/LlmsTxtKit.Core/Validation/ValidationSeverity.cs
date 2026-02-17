namespace LlmsTxtKit.Core.Validation;

/// <summary>
/// Indicates the severity of a validation finding.
/// </summary>
/// <remarks>
/// The distinction between errors and warnings is significant:
/// <see cref="ValidationReport.IsValid"/> returns <c>true</c> only when there
/// are zero <see cref="Error"/>-severity issues. Warnings do not invalidate
/// a document — callers choose their own warning tolerance.
/// </remarks>
public enum ValidationSeverity
{
    /// <summary>
    /// A non-fatal issue. The document is structurally valid per the spec but
    /// contains something suspicious (empty sections, stale content, redirecting
    /// URLs). Warnings do not affect <see cref="ValidationReport.IsValid"/>.
    /// </summary>
    Warning,

    /// <summary>
    /// A structural problem that means the document is invalid per the llms.txt
    /// specification (missing required H1, invalid URLs). Errors cause
    /// <see cref="ValidationReport.IsValid"/> to return <c>false</c>.
    /// </summary>
    Error
}
