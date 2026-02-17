namespace LlmsTxtKit.Core.Context;

/// <summary>
/// Configuration options for <see cref="ContextGenerator"/>. Controls the token
/// budget, Optional section handling, XML wrapping, and token estimation strategy.
/// </summary>
/// <remarks>
/// <para>
/// The defaults are chosen to match the reference Python implementation's behavior:
/// Optional sections are excluded by default (matching <c>create_ctx(optional=False)</c>),
/// XML wrapping is enabled, and the token estimator uses a words÷4 heuristic that
/// is reasonably accurate across most English-language tokenizers.
/// </para>
/// <para>
/// For production use with a specific LLM, consider providing a custom
/// <see cref="TokenEstimator"/> that uses the model's actual tokenizer for
/// precise budget enforcement. This avoids shipping model-specific tokenizer
/// dependencies in the Core library (per PRS NG-6).
/// </para>
/// </remarks>
public sealed class ContextOptions
{
    /// <summary>
    /// Approximate token budget for the generated context. When set, the generator
    /// will enforce this limit by first dropping Optional sections, then truncating
    /// remaining sections at sentence boundaries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <c>null</c> (the default), no token limit is applied and all content
    /// is included. Set this to your model's context window size minus the space
    /// needed for the prompt and expected response.
    /// </para>
    /// <para>
    /// The budget is approximate because token counting depends on the specific
    /// tokenizer. The default heuristic (words÷4) is within ~10-20% of most
    /// tokenizers for English text. For tighter budget adherence, provide a
    /// custom <see cref="TokenEstimator"/>.
    /// </para>
    /// </remarks>
    public int? MaxTokens { get; set; }

    /// <summary>
    /// Whether to include the <c>## Optional</c> section content in the generated
    /// context. Default: <c>false</c>, matching the reference implementation's
    /// <c>create_ctx(optional=False)</c> behavior.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The llms.txt specification designates <c>## Optional</c> as a section
    /// containing content that is "nice to have" but not essential. When token
    /// budgets are tight, this section is the first to be dropped.
    /// </para>
    /// <para>
    /// Even when <c>IncludeOptional == true</c>, if a token budget is set and
    /// the total content exceeds it, the Optional section is dropped before
    /// any other section is truncated.
    /// </para>
    /// </remarks>
    public bool IncludeOptional { get; set; } = false;

    /// <summary>
    /// Whether to wrap each section's content in XML <c>&lt;section name="..."&gt;</c>
    /// tags. Default: <c>true</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When enabled, each section's content is wrapped as:
    /// <code>
    /// &lt;section name="SectionName"&gt;
    /// [concatenated content of all entries in this section]
    /// &lt;/section&gt;
    /// </code>
    /// </para>
    /// <para>
    /// This generic wrapper approach (vs. the reference implementation's
    /// <c>&lt;project&gt;/&lt;section&gt;/&lt;doc&gt;</c> hierarchy) avoids
    /// generating arbitrary XML element names from user content, which could
    /// produce invalid XML if section names contain special characters. See
    /// Design Spec § 2.5 for the rationale.
    /// </para>
    /// <para>
    /// When disabled, sections are separated by blank lines with no XML tags.
    /// </para>
    /// </remarks>
    public bool WrapSectionsInXml { get; set; } = true;

    /// <summary>
    /// Custom token counting function. When provided, this function is called
    /// on each piece of content to estimate its token count. When <c>null</c>
    /// (the default), the generator uses a words÷4 heuristic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The function receives a string of content and returns the estimated
    /// token count. For example, to use tiktoken for GPT models:
    /// <code>
    /// options.TokenEstimator = text => tiktoken.CountTokens(text);
    /// </code>
    /// </para>
    /// <para>
    /// By accepting a <c>Func&lt;string, int&gt;</c> rather than shipping a
    /// tokenizer, the Core library avoids model-specific dependencies while
    /// still supporting precise budget enforcement (per PRS NG-6).
    /// </para>
    /// </remarks>
    public Func<string, int>? TokenEstimator { get; set; }
}
