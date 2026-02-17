using LlmsTxtKit.Core.Parsing;

namespace LlmsTxtKit.Core.Validation;

/// <summary>
/// Defines a single, independently-testable validation rule for llms.txt documents.
/// </summary>
/// <remarks>
/// <para>
/// Each validation rule is an independent class implementing this interface.
/// This makes rules individually testable, individually configurable, and
/// independently extensible. Adding a new rule is additive — it doesn't
/// touch existing rule logic.
/// </para>
/// <para>
/// Rules may be synchronous (structural checks against the in-memory document)
/// or asynchronous (network checks like URL reachability). The async signature
/// accommodates both — synchronous rules can return <c>Task.FromResult</c>.
/// </para>
/// </remarks>
public interface IValidationRule
{
    /// <summary>
    /// Evaluates this rule against the provided document and returns any
    /// issues found.
    /// </summary>
    /// <param name="document">The parsed llms.txt document to validate.</param>
    /// <param name="options">
    /// Validation options that may control whether this rule runs
    /// (e.g., URL checking rules only run when <see cref="ValidationOptions.CheckLinkedUrls"/>
    /// is <c>true</c>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token for async rules.</param>
    /// <returns>
    /// Zero or more <see cref="ValidationIssue"/> instances. An empty enumerable
    /// means the document passed this rule with no issues.
    /// </returns>
    Task<IEnumerable<ValidationIssue>> EvaluateAsync(
        LlmsDocument document,
        ValidationOptions options,
        CancellationToken cancellationToken = default);
}
