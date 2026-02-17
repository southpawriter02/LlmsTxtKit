namespace LlmsTxtKit.Core.Context;

/// <summary>
/// The result of context generation from a parsed llms.txt document. Contains
/// the generated context string plus metadata about token usage, section
/// inclusion, and any fetch errors encountered.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Content"/> property contains the complete, ready-to-inject
/// context string. When XML wrapping is enabled, it includes
/// <c>&lt;section name="..."&gt;</c> tags around each section's content.
/// </para>
/// <para>
/// The metadata properties (<see cref="SectionsIncluded"/>,
/// <see cref="SectionsOmitted"/>, <see cref="SectionsTruncated"/>,
/// <see cref="FetchErrors"/>) allow callers to understand what happened
/// during generation — which sections made it in, which were dropped for
/// budget reasons, and which URLs failed to fetch.
/// </para>
/// </remarks>
public sealed class ContextResult
{
    /// <summary>
    /// The generated context string, ready to be injected into an LLM prompt.
    /// Contains the concatenated content of all included sections, optionally
    /// wrapped in XML tags.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// The estimated token count of <see cref="Content"/>, calculated using
    /// the configured token estimator (or the default words÷4 heuristic).
    /// </summary>
    public required int ApproximateTokenCount { get; init; }

    /// <summary>
    /// Names of sections that were fully included in the generated context.
    /// </summary>
    public required IReadOnlyList<string> SectionsIncluded { get; init; }

    /// <summary>
    /// Names of sections that were dropped entirely due to token budget
    /// constraints. The Optional section (if present and excluded by budget)
    /// will appear here.
    /// </summary>
    public required IReadOnlyList<string> SectionsOmitted { get; init; }

    /// <summary>
    /// Names of sections that were partially included (truncated at a sentence
    /// boundary to fit within the token budget).
    /// </summary>
    public required IReadOnlyList<string> SectionsTruncated { get; init; }

    /// <summary>
    /// Entries whose linked URLs failed to fetch, with error details. Each item
    /// contains the URL that failed and a human-readable error description.
    /// </summary>
    /// <remarks>
    /// Failed URLs are not silently omitted from the generated content — they
    /// are included as error placeholders so the LLM knows content is missing.
    /// This list provides structured access to the same error information.
    /// </remarks>
    public required IReadOnlyList<FetchError> FetchErrors { get; init; }

    /// <summary>
    /// A convenience factory for an empty result (no content, no sections, no errors).
    /// </summary>
    public static ContextResult Empty => new()
    {
        Content = string.Empty,
        ApproximateTokenCount = 0,
        SectionsIncluded = Array.Empty<string>(),
        SectionsOmitted = Array.Empty<string>(),
        SectionsTruncated = Array.Empty<string>(),
        FetchErrors = Array.Empty<FetchError>()
    };
}

/// <summary>
/// Represents a URL that failed to fetch during context generation, along
/// with a human-readable error description.
/// </summary>
/// <param name="Url">The URL that failed to fetch.</param>
/// <param name="ErrorMessage">A human-readable description of why the fetch failed.</param>
public sealed record FetchError(Uri Url, string ErrorMessage);
