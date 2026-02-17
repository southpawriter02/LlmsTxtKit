namespace LlmsTxtKit.Core.Parsing;

/// <summary>
/// Represents a single entry within an llms.txt section — a link to a
/// resource with an optional title and description.
/// </summary>
/// <remarks>
/// Entries are parsed from Markdown list items matching the pattern:
/// <c>- [Title](https://url): Optional description</c>.
/// The colon-separated description is optional per the spec.
/// </remarks>
/// <param name="Url">The link target URI.</param>
/// <param name="Title">The link text, or <c>null</c> if not present.</param>
/// <param name="Description">
/// The text after the colon separator, or <c>null</c> if no description was provided.
/// </param>
public sealed record LlmsEntry(
    Uri Url,
    string? Title,
    string? Description);
