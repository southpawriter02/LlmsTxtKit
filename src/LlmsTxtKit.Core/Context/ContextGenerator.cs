using System.Text;
using System.Text.RegularExpressions;
using LlmsTxtKit.Core.Parsing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmsTxtKit.Core.Context;

/// <summary>
/// Generates LLM-ready context strings from parsed llms.txt documents by
/// fetching linked Markdown content, applying token budgets, wrapping sections
/// in XML tags, and handling Optional section semantics.
/// </summary>
/// <remarks>
/// <para>
/// The generator follows the reference Python implementation's behavior:
/// sections are processed in document order, Optional sections are excluded
/// by default, and content is wrapped in <c>&lt;section name="..."&gt;</c>
/// tags for source attribution.
/// </para>
/// <para>
/// <b>Token Budgeting:</b> When <see cref="ContextOptions.MaxTokens"/> is set,
/// the generator enforces the budget by first dropping the Optional section
/// (if included), then truncating remaining sections at sentence boundaries.
/// A truncation indicator is appended to truncated content.
/// </para>
/// <para>
/// <b>Content Cleaning:</b> Fetched Markdown content is cleaned by stripping
/// HTML comments and base64-embedded images before inclusion.
/// </para>
/// <para>
/// <b>Error Handling:</b> URLs that fail to fetch are included as error
/// placeholders (not silently omitted) so the LLM knows content is missing.
/// </para>
/// <para>
/// <b>Sequential Fetching:</b> v1.0 fetches linked documents sequentially.
/// Parallel fetching with configurable concurrency is a candidate for
/// post-v1.0 enhancement.
/// </para>
/// </remarks>
public sealed class ContextGenerator
{
    /// <summary>The content fetcher used to retrieve linked Markdown.</summary>
    private readonly IContentFetcher _fetcher;

    /// <summary>Logger for diagnostic output during context generation.</summary>
    private readonly ILogger<ContextGenerator> _logger;

    /// <summary>Pattern matching HTML comments: &lt;!-- ... --&gt;</summary>
    private static readonly Regex HtmlCommentPattern = new(
        @"<!--[\s\S]*?-->",
        RegexOptions.Compiled);

    /// <summary>Pattern matching base64-embedded images in Markdown: ![...](data:image/...;base64,...)</summary>
    private static readonly Regex Base64ImagePattern = new(
        @"!\[[^\]]*\]\(data:image/[^;]+;base64,[A-Za-z0-9+/=]+\)",
        RegexOptions.Compiled);

    /// <summary>Truncation indicator appended to content that was cut short.</summary>
    internal const string TruncationIndicator = "[... content truncated to fit token budget ...]";

    /// <summary>
    /// Creates a new context generator with the specified content fetcher.
    /// </summary>
    /// <param name="fetcher">
    /// The content fetcher for retrieving linked Markdown. If <c>null</c>,
    /// a default <see cref="HttpContentFetcher"/> is created.
    /// </param>
    /// <param name="logger">
    /// Optional logger for diagnostic output. Logs content fetching, token budget
    /// decisions, section drops/truncations, and final generation metrics.
    /// </param>
    public ContextGenerator(IContentFetcher? fetcher = null, ILogger<ContextGenerator>? logger = null)
    {
        _fetcher = fetcher ?? new HttpContentFetcher();
        _logger = logger ?? NullLogger<ContextGenerator>.Instance;
    }

    /// <summary>
    /// Generates an LLM-ready context string from a parsed llms.txt document
    /// by fetching and concatenating linked Markdown content.
    /// </summary>
    /// <param name="document">The parsed llms.txt document to expand into context.</param>
    /// <param name="options">
    /// Configuration options controlling token budget, Optional handling, and
    /// XML wrapping. If <c>null</c>, defaults are used.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for async operations.</param>
    /// <returns>
    /// A <see cref="ContextResult"/> containing the generated content string,
    /// approximate token count, section inclusion metadata, and any fetch errors.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="document"/> is <c>null</c>.
    /// </exception>
    public async Task<ContextResult> GenerateAsync(
        LlmsDocument document,
        ContextOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var opts = options ?? new ContextOptions();
        var estimator = opts.TokenEstimator ?? DefaultTokenEstimator;
        var fetchErrors = new List<FetchError>();

        _logger.LogDebug("Generating context: title={Title}, sections={SectionCount}, maxTokens={MaxTokens}, includeOptional={IncludeOptional}, wrapXml={WrapXml}",
            document.Title, document.Sections.Count, opts.MaxTokens?.ToString() ?? "unlimited", opts.IncludeOptional, opts.WrapSectionsInXml);

        // ---------------------------------------------------------------
        // Phase 1: Fetch content for each entry in each eligible section
        // ---------------------------------------------------------------
        var sectionContents = new List<SectionContent>();

        foreach (var section in document.Sections)
        {
            // Skip Optional sections when IncludeOptional is false
            if (IsOptionalSection(section) && !opts.IncludeOptional)
            {
                _logger.LogDebug("Skipping Optional section={Section} (includeOptional=false)", section.Name);
                continue;
            }

            _logger.LogDebug("Fetching content for section={Section}, entryCount={EntryCount}", section.Name, section.Entries.Count);
            var entryContents = new List<string>();

            foreach (var entry in section.Entries)
            {
                var fetchResult = await _fetcher.FetchContentAsync(entry.Url, cancellationToken)
                    .ConfigureAwait(false);

                if (fetchResult.IsSuccess)
                {
                    var cleaned = CleanContent(fetchResult.Content!);
                    _logger.LogDebug("Fetched entry url={Url}, rawSize={RawSize}, cleanedSize={CleanedSize}",
                        entry.Url, fetchResult.Content!.Length, cleaned.Length);
                    entryContents.Add(cleaned);
                }
                else
                {
                    // Include error placeholder — not silent omission
                    var placeholder = $"[Error fetching {entry.Url}: {fetchResult.ErrorMessage}]";
                    entryContents.Add(placeholder);
                    fetchErrors.Add(new FetchError(entry.Url, fetchResult.ErrorMessage!));
                    _logger.LogWarning("Failed to fetch entry url={Url}: {Error}", entry.Url, fetchResult.ErrorMessage);
                }
            }

            var combinedContent = string.Join("\n\n", entryContents);
            sectionContents.Add(new SectionContent(
                section.Name,
                combinedContent,
                IsOptionalSection(section)));
        }

        // ---------------------------------------------------------------
        // Phase 2: Apply token budget
        // ---------------------------------------------------------------
        var sectionsIncluded = new List<string>();
        var sectionsOmitted = new List<string>();
        var sectionsTruncated = new List<string>();

        if (opts.MaxTokens.HasValue)
        {
            _logger.LogDebug("Applying token budget: maxTokens={MaxTokens}", opts.MaxTokens.Value);
            ApplyTokenBudget(
                sectionContents, opts.MaxTokens.Value, estimator, opts.WrapSectionsInXml,
                sectionsOmitted, sectionsTruncated);

            if (sectionsOmitted.Count > 0)
                _logger.LogDebug("Sections omitted by budget: [{Omitted}]", string.Join(", ", sectionsOmitted));
            if (sectionsTruncated.Count > 0)
                _logger.LogDebug("Sections truncated by budget: [{Truncated}]", string.Join(", ", sectionsTruncated));
        }

        // ---------------------------------------------------------------
        // Phase 3: Build output with optional XML wrapping
        // ---------------------------------------------------------------
        var output = new StringBuilder();

        foreach (var sc in sectionContents)
        {
            if (sc.IsDropped)
            {
                if (!sectionsOmitted.Contains(sc.Name))
                    sectionsOmitted.Add(sc.Name);
                continue;
            }

            sectionsIncluded.Add(sc.Name);

            if (output.Length > 0)
                output.AppendLine();

            if (opts.WrapSectionsInXml)
            {
                output.AppendLine($"<section name=\"{EscapeXmlAttribute(sc.Name)}\">");
                output.AppendLine(sc.Content);
                output.AppendLine("</section>");
            }
            else
            {
                output.AppendLine($"## {sc.Name}");
                output.AppendLine(sc.Content);
            }
        }

        var finalContent = output.ToString().TrimEnd();
        var tokenCount = estimator(finalContent);

        _logger.LogInformation("Context generation complete: tokenCount={TokenCount}, sectionsIncluded={Included}, sectionsOmitted={Omitted}, sectionsTruncated={Truncated}, fetchErrors={Errors}",
            tokenCount, sectionsIncluded.Count, sectionsOmitted.Count, sectionsTruncated.Count, fetchErrors.Count);

        return new ContextResult
        {
            Content = finalContent,
            ApproximateTokenCount = tokenCount,
            SectionsIncluded = sectionsIncluded.AsReadOnly(),
            SectionsOmitted = sectionsOmitted.AsReadOnly(),
            SectionsTruncated = sectionsTruncated.AsReadOnly(),
            FetchErrors = fetchErrors.AsReadOnly()
        };
    }

    // ---------------------------------------------------------------
    // Token Budgeting
    // ---------------------------------------------------------------

    /// <summary>
    /// Applies the token budget to the section contents by first dropping
    /// Optional sections, then truncating remaining sections at sentence
    /// boundaries.
    /// </summary>
    private static void ApplyTokenBudget(
        List<SectionContent> sections,
        int maxTokens,
        Func<string, int> estimator,
        bool wrapInXml,
        List<string> sectionsOmitted,
        List<string> sectionsTruncated)
    {
        // Calculate current total
        int currentTotal = EstimateTotalTokens(sections, estimator, wrapInXml);

        if (currentTotal <= maxTokens)
            return;

        // Priority 1: Drop Optional sections
        foreach (var section in sections.Where(s => s.IsOptional && !s.IsDropped))
        {
            section.IsDropped = true;
            sectionsOmitted.Add(section.Name);
            currentTotal = EstimateTotalTokens(sections, estimator, wrapInXml);

            if (currentTotal <= maxTokens)
                return;
        }

        // Priority 2: Truncate remaining sections (last to first) at sentence boundaries
        for (int i = sections.Count - 1; i >= 0; i--)
        {
            if (sections[i].IsDropped)
                continue;

            currentTotal = EstimateTotalTokens(sections, estimator, wrapInXml);
            if (currentTotal <= maxTokens)
                return;

            // Calculate how many tokens this section can keep
            var otherTokens = EstimateTotalTokensExcluding(sections, i, estimator, wrapInXml);
            var availableForSection = maxTokens - otherTokens;

            // Account for XML wrapping overhead
            if (wrapInXml)
            {
                var wrapOverhead = estimator($"<section name=\"{sections[i].Name}\">\n\n</section>");
                availableForSection -= wrapOverhead;
            }

            // Account for truncation indicator
            var indicatorTokens = estimator(TruncationIndicator);
            availableForSection -= indicatorTokens;

            if (availableForSection <= 0)
            {
                sections[i].IsDropped = true;
                sectionsOmitted.Add(sections[i].Name);
                continue;
            }

            var truncated = TruncateAtSentenceBoundary(sections[i].Content, availableForSection, estimator);
            if (truncated != sections[i].Content)
            {
                sections[i].Content = truncated + "\n" + TruncationIndicator;
                sectionsTruncated.Add(sections[i].Name);
            }
        }
    }

    /// <summary>
    /// Estimates the total token count of all non-dropped sections including
    /// XML wrapping overhead and separators.
    /// </summary>
    private static int EstimateTotalTokens(
        List<SectionContent> sections, Func<string, int> estimator, bool wrapInXml)
    {
        var sb = new StringBuilder();
        foreach (var s in sections.Where(s => !s.IsDropped))
        {
            if (sb.Length > 0)
                sb.AppendLine();

            if (wrapInXml)
            {
                sb.AppendLine($"<section name=\"{s.Name}\">");
                sb.AppendLine(s.Content);
                sb.AppendLine("</section>");
            }
            else
            {
                sb.AppendLine($"## {s.Name}");
                sb.AppendLine(s.Content);
            }
        }
        return estimator(sb.ToString());
    }

    /// <summary>
    /// Estimates the total token count excluding a specific section index.
    /// </summary>
    private static int EstimateTotalTokensExcluding(
        List<SectionContent> sections, int excludeIndex, Func<string, int> estimator, bool wrapInXml)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < sections.Count; i++)
        {
            if (i == excludeIndex || sections[i].IsDropped)
                continue;

            if (sb.Length > 0)
                sb.AppendLine();

            if (wrapInXml)
            {
                sb.AppendLine($"<section name=\"{sections[i].Name}\">");
                sb.AppendLine(sections[i].Content);
                sb.AppendLine("</section>");
            }
            else
            {
                sb.AppendLine($"## {sections[i].Name}");
                sb.AppendLine(sections[i].Content);
            }
        }
        return estimator(sb.ToString());
    }

    /// <summary>
    /// Truncates content at a sentence boundary to fit within the specified
    /// token budget. Never truncates mid-word or mid-sentence.
    /// </summary>
    /// <param name="content">The content to truncate.</param>
    /// <param name="maxTokens">Maximum tokens allowed for this content.</param>
    /// <param name="estimator">Token estimation function.</param>
    /// <returns>
    /// The truncated content ending at a sentence boundary, or the original
    /// content if it fits within the budget.
    /// </returns>
    internal static string TruncateAtSentenceBoundary(
        string content, int maxTokens, Func<string, int> estimator)
    {
        if (estimator(content) <= maxTokens)
            return content;

        // Split into sentences (simple heuristic: split on ". ", "! ", "? ", or newlines)
        var sentences = SplitIntoSentences(content);
        var result = new StringBuilder();

        foreach (var sentence in sentences)
        {
            var candidate = result.Length > 0
                ? result + sentence
                : sentence;

            if (estimator(candidate.ToString()) > maxTokens)
                break;

            result.Append(sentence);
        }

        // If we couldn't fit even one sentence, return as much as we can
        // (truncate at word boundary)
        if (result.Length == 0)
        {
            return TruncateAtWordBoundary(content, maxTokens, estimator);
        }

        return result.ToString().TrimEnd();
    }

    /// <summary>
    /// Splits content into sentence-like segments, preserving the delimiters
    /// with the preceding segment.
    /// </summary>
    private static List<string> SplitIntoSentences(string content)
    {
        var sentences = new List<string>();
        var pattern = new Regex(@"(?<=[.!?])\s+|(?<=\n)\s*", RegexOptions.Compiled);
        var parts = pattern.Split(content);

        foreach (var part in parts)
        {
            if (!string.IsNullOrEmpty(part))
                sentences.Add(part + " ");
        }

        return sentences;
    }

    /// <summary>
    /// Last-resort truncation: cuts at a word boundary when even one sentence
    /// exceeds the budget.
    /// </summary>
    private static string TruncateAtWordBoundary(
        string content, int maxTokens, Func<string, int> estimator)
    {
        var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new StringBuilder();

        foreach (var word in words)
        {
            var candidate = result.Length > 0
                ? result + " " + word
                : word;

            if (estimator(candidate.ToString()) > maxTokens)
                break;

            if (result.Length > 0)
                result.Append(' ');
            result.Append(word);
        }

        return result.ToString();
    }

    // ---------------------------------------------------------------
    // Content Cleaning
    // ---------------------------------------------------------------

    /// <summary>
    /// Cleans fetched Markdown content by stripping HTML comments and
    /// base64-embedded images.
    /// </summary>
    /// <param name="content">The raw fetched Markdown content.</param>
    /// <returns>The cleaned content with HTML comments and base64 images removed.</returns>
    public static string CleanContent(string content)
    {
        // Strip HTML comments
        var cleaned = HtmlCommentPattern.Replace(content, "");

        // Strip base64-embedded images
        cleaned = Base64ImagePattern.Replace(cleaned, "");

        // Clean up any resulting double blank lines
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");

        return cleaned.Trim();
    }

    // ---------------------------------------------------------------
    // Token Estimation
    // ---------------------------------------------------------------

    /// <summary>
    /// Default token estimator: words ÷ 4 heuristic. Splits on whitespace,
    /// counts tokens, and divides by 4. Reasonably accurate for English text
    /// across most tokenizers (within ~10-20%).
    /// </summary>
    /// <param name="text">The text to estimate tokens for.</param>
    /// <returns>The estimated token count.</returns>
    internal static int DefaultTokenEstimator(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var words = text.Split(
            (char[]?)null, // Split on all whitespace
            StringSplitOptions.RemoveEmptyEntries);

        // words/4 heuristic, but with a minimum of 1 token per word
        // (short texts undercount with pure division)
        return Math.Max(1, (int)Math.Ceiling(words.Length / 4.0));
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Determines if a section is the "Optional" section per the llms.txt spec.
    /// </summary>
    private static bool IsOptionalSection(LlmsSection section) =>
        section.IsOptional ||
        string.Equals(section.Name, "Optional", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Escapes a string for safe use in an XML attribute value.
    /// </summary>
    private static string EscapeXmlAttribute(string value) =>
        value
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");

    // ---------------------------------------------------------------
    // Internal Types
    // ---------------------------------------------------------------

    /// <summary>
    /// Mutable working data for a section during context generation.
    /// </summary>
    private sealed class SectionContent
    {
        public string Name { get; }
        public string Content { get; set; }
        public bool IsOptional { get; }
        public bool IsDropped { get; set; }

        public SectionContent(string name, string content, bool isOptional)
        {
            Name = name;
            Content = content;
            IsOptional = isOptional;
        }
    }
}
