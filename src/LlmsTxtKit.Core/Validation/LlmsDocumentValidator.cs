using LlmsTxtKit.Core.Parsing;
using LlmsTxtKit.Core.Validation.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmsTxtKit.Core.Validation;

/// <summary>
/// Orchestrates validation of an llms.txt document by running all registered
/// <see cref="IValidationRule"/> implementations and aggregating their results
/// into a <see cref="ValidationReport"/>.
/// </summary>
/// <remarks>
/// <para>
/// By default, the validator registers all 10 built-in rules defined in
/// Design Spec § 2.3. Custom rules can be added via <see cref="AddRule"/>.
/// </para>
/// <para>
/// Structural rules (those that inspect the in-memory document without making
/// network requests) always run. Network-dependent rules (URL reachability,
/// freshness) are controlled by <see cref="ValidationOptions"/> and only
/// execute when their corresponding option is enabled.
/// </para>
/// </remarks>
public sealed class LlmsDocumentValidator
{
    /// <summary>
    /// The set of validation rules to run. Initialized with the 10 built-in rules.
    /// </summary>
    private readonly List<IValidationRule> _rules;

    /// <summary>Logger for validation flow and rule execution diagnostics.</summary>
    private readonly ILogger<LlmsDocumentValidator> _logger;

    /// <summary>
    /// Creates a new validator with all 10 built-in validation rules registered.
    /// </summary>
    /// <param name="httpClient">
    /// Optional <see cref="HttpClient"/> for network-dependent rules (URL
    /// reachability, redirect detection, freshness). If <c>null</c>, rules
    /// create their own clients as needed.
    /// </param>
    /// <param name="logger">
    /// Optional logger for validation diagnostics. Logs which rules execute,
    /// issue counts per rule, and final validation summary.
    /// </param>
    public LlmsDocumentValidator(HttpClient? httpClient = null, ILogger<LlmsDocumentValidator>? logger = null)
    {
        _logger = logger ?? NullLogger<LlmsDocumentValidator>.Instance;
        _rules = new List<IValidationRule>
        {
            // -- Error-severity rules (structural) --
            new RequiredH1MissingRule(),
            new MultipleH1FoundRule(),
            new EntryUrlInvalidRule(),

            // -- Warning-severity rules (structural) --
            new BlockquoteMalformedRule(),
            new SectionEmptyRule(),
            new UnexpectedHeadingLevelRule(),
            new ContentOutsideStructureRule(),

            // -- Warning-severity rules (network-dependent) --
            new EntryUrlUnreachableRule(httpClient),
            new EntryUrlRedirectedRule(httpClient),
            new ContentStaleRule(httpClient)
        };
    }

    /// <summary>
    /// Adds a custom validation rule to the set of rules that will be evaluated.
    /// Custom rules are evaluated after all built-in rules.
    /// </summary>
    /// <param name="rule">The custom rule to add.</param>
    /// <returns>This validator instance (for fluent chaining).</returns>
    public LlmsDocumentValidator AddRule(IValidationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _rules.Add(rule);
        return this;
    }

    /// <summary>
    /// Validates the provided document by running all registered rules and
    /// returns an aggregated <see cref="ValidationReport"/>.
    /// </summary>
    /// <param name="document">The parsed llms.txt document to validate.</param>
    /// <param name="options">
    /// Validation options controlling which rules run. If <c>null</c>,
    /// default options are used (structural checks only, no network checks).
    /// </param>
    /// <param name="cancellationToken">Cancellation token for async rules.</param>
    /// <returns>
    /// A <see cref="ValidationReport"/> containing all errors and warnings
    /// found across all rules. <see cref="ValidationReport.IsValid"/> is
    /// <c>true</c> only when zero errors are present.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="document"/> is <c>null</c>.
    /// </exception>
    public async Task<ValidationReport> ValidateAsync(
        LlmsDocument document,
        ValidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var effectiveOptions = options ?? new ValidationOptions();
        var allIssues = new List<ValidationIssue>();

        _logger.LogDebug("Starting validation: title={Title}, ruleCount={RuleCount}, checkUrls={CheckUrls}, checkFreshness={CheckFreshness}",
            document.Title, _rules.Count, effectiveOptions.CheckLinkedUrls, effectiveOptions.CheckFreshness);

        // Run each rule and collect all issues
        foreach (var rule in _rules)
        {
            var ruleName = rule.GetType().Name;
            var issues = await rule.EvaluateAsync(document, effectiveOptions, cancellationToken)
                .ConfigureAwait(false);
            var issueList = issues.ToList();

            if (issueList.Count > 0)
                _logger.LogDebug("Rule {Rule} produced {IssueCount} issue(s)", ruleName, issueList.Count);

            allIssues.AddRange(issueList);
        }

        // Sort: errors first, then warnings, preserving discovery order within each severity
        allIssues.Sort((a, b) =>
        {
            // Error (1) should come before Warning (0), so reverse the comparison
            int severityComparison = b.Severity.CompareTo(a.Severity);
            return severityComparison;
        });

        var errorCount = allIssues.Count(i => i.Severity == ValidationSeverity.Error);
        var warningCount = allIssues.Count(i => i.Severity == ValidationSeverity.Warning);
        _logger.LogInformation("Validation complete: isValid={IsValid}, errors={Errors}, warnings={Warnings}",
            errorCount == 0, errorCount, warningCount);

        return new ValidationReport { AllIssues = allIssues.AsReadOnly() };
    }
}
