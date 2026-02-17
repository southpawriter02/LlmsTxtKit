namespace LlmsTxtKit.Core.Validation;

/// <summary>
/// Represents a single finding from the validation of an llms.txt document.
/// Each issue has a severity, a machine-readable rule ID, a human-readable
/// message, and an optional location within the document.
/// </summary>
/// <param name="Severity">The severity level of this finding.</param>
/// <param name="Rule">
/// A machine-readable rule identifier (e.g., <c>"REQUIRED_H1_MISSING"</c>).
/// These IDs are stable across versions and can be used for filtering,
/// suppression, or programmatic handling of specific rule violations.
/// </param>
/// <param name="Message">A human-readable description of the issue.</param>
/// <param name="Location">
/// Where in the document the issue was detected, or <c>null</c> if the issue
/// is not localized. May contain a line number, section name, or entry URL
/// depending on the rule that produced it.
/// </param>
public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Rule,
    string Message,
    string? Location = null);
