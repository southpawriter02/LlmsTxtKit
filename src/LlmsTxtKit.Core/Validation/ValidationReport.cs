namespace LlmsTxtKit.Core.Validation;

/// <summary>
/// Aggregates the results of validating an llms.txt document. Contains all
/// errors and warnings found, and a summary <see cref="IsValid"/> flag.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsValid"/> is <c>true</c> when there are zero
/// <see cref="ValidationSeverity.Error"/>-severity issues. Warnings alone
/// do not invalidate a document — callers choose their own warning tolerance.
/// </para>
/// <para>
/// Use <see cref="Errors"/> and <see cref="Warnings"/> for filtered access,
/// or <see cref="AllIssues"/> for the combined, severity-sorted list.
/// </para>
/// </remarks>
public sealed class ValidationReport
{
    /// <summary>
    /// All validation issues found, sorted by severity (errors first) then
    /// by the order they were discovered.
    /// </summary>
    public required IReadOnlyList<ValidationIssue> AllIssues { get; init; }

    /// <summary>
    /// <c>true</c> if the document has zero <see cref="ValidationSeverity.Error"/>
    /// issues. Warnings do not affect this flag.
    /// </summary>
    public bool IsValid => !Errors.Any();

    /// <summary>
    /// Only the <see cref="ValidationSeverity.Error"/> issues.
    /// </summary>
    public IReadOnlyList<ValidationIssue> Errors =>
        AllIssues.Where(i => i.Severity == ValidationSeverity.Error).ToList().AsReadOnly();

    /// <summary>
    /// Only the <see cref="ValidationSeverity.Warning"/> issues.
    /// </summary>
    public IReadOnlyList<ValidationIssue> Warnings =>
        AllIssues.Where(i => i.Severity == ValidationSeverity.Warning).ToList().AsReadOnly();

    /// <summary>
    /// Creates an empty report representing a fully valid document.
    /// </summary>
    public static ValidationReport Empty => new() { AllIssues = [] };
}
