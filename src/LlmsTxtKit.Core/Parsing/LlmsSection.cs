namespace LlmsTxtKit.Core.Parsing;

/// <summary>
/// Represents an H2-delimited section within an llms.txt document.
/// Each section contains a name (the H2 heading text) and zero or more
/// <see cref="LlmsEntry"/> items representing linked resources.
/// </summary>
/// <param name="Name">The H2 heading text that identifies this section.</param>
/// <param name="IsOptional">
/// <c>true</c> if this section is titled exactly "Optional" (case-sensitive).
/// Optional sections may be skipped when generating abbreviated context.
/// </param>
/// <param name="Entries">The ordered list of link entries in this section.</param>
public sealed record LlmsSection(
    string Name,
    bool IsOptional,
    IReadOnlyList<LlmsEntry> Entries);
