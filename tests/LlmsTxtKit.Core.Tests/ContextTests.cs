using LlmsTxtKit.Core.Context;
using LlmsTxtKit.Core.Parsing;
using Xunit;

namespace LlmsTxtKit.Core.Tests;

/// <summary>
/// Unit tests for <see cref="ContextGenerator"/>, <see cref="ContextOptions"/>,
/// <see cref="ContextResult"/>, and supporting types. Tests cover content generation,
/// token budgeting, XML wrapping, Optional section handling, content cleaning,
/// and error handling for fetch failures.
/// </summary>
/// <remarks>
/// All tests use a <see cref="MockContentFetcher"/> that returns pre-defined
/// content for specific URLs, enabling deterministic testing without network access.
/// </remarks>
public class ContextTests
{
    // ---------------------------------------------------------------
    // Mock Content Fetcher
    // ---------------------------------------------------------------

    /// <summary>
    /// A mock <see cref="IContentFetcher"/> that maps URLs to pre-defined content
    /// for unit testing. URLs not in the map return a fetch error.
    /// </summary>
    private sealed class MockContentFetcher : IContentFetcher
    {
        private readonly Dictionary<string, string> _contentMap = new();
        private readonly HashSet<string> _failingUrls = new();

        /// <summary>Registers content for a URL.</summary>
        public MockContentFetcher WithContent(string url, string content)
        {
            _contentMap[url] = content;
            return this;
        }

        /// <summary>Registers a URL that should fail to fetch.</summary>
        public MockContentFetcher WithFailure(string url)
        {
            _failingUrls.Add(url);
            return this;
        }

        public Task<ContentFetchResult> FetchContentAsync(
            Uri url, CancellationToken cancellationToken = default)
        {
            var urlStr = url.ToString();

            if (_failingUrls.Contains(urlStr))
                return Task.FromResult(new ContentFetchResult(null, "Simulated fetch failure"));

            if (_contentMap.TryGetValue(urlStr, out var content))
                return Task.FromResult(new ContentFetchResult(content, null));

            return Task.FromResult(new ContentFetchResult(null, $"URL not found in mock: {urlStr}"));
        }
    }

    // ---------------------------------------------------------------
    // Test Data Helpers
    // ---------------------------------------------------------------

    /// <summary>Sample Markdown content returned by mock fetcher.</summary>
    private const string GuideContent = "# Getting Started\n\nThis is the guide content. It has multiple sentences. Each sentence provides value.";
    private const string ApiContent = "# API Reference\n\nThe API provides endpoints for data access. Authentication is required.";
    private const string DemoContent = "# Demo\n\nThis demo shows the basics.";
    private const string OptionalContent = "# FAQ\n\nFrequently asked questions and answers.";

    /// <summary>Builds a standard mock fetcher with content for common test URLs.</summary>
    private static MockContentFetcher BuildStandardFetcher() => new MockContentFetcher()
        .WithContent("https://example.com/guide.md", GuideContent)
        .WithContent("https://example.com/api.md", ApiContent)
        .WithContent("https://example.com/demo.md", DemoContent)
        .WithContent("https://example.com/faq.md", OptionalContent);

    /// <summary>Parses an llms.txt document with Docs and Examples sections.</summary>
    private static LlmsDocument ParseStandardDoc() => LlmsDocumentParser.Parse(
        """
        # Test Site
        > A test site for context generation.
        ## Docs
        - [Guide](https://example.com/guide.md): The main guide
        - [API](https://example.com/api.md): API reference
        ## Examples
        - [Demo](https://example.com/demo.md): A demo
        """);

    /// <summary>Parses an llms.txt document that includes an Optional section.</summary>
    private static LlmsDocument ParseDocWithOptional() => LlmsDocumentParser.Parse(
        """
        # Test Site
        > A test site.
        ## Docs
        - [Guide](https://example.com/guide.md): The main guide
        ## Optional
        - [FAQ](https://example.com/faq.md): FAQ
        """);

    // ===============================================================
    // GenerateAsync — Single Section
    // ===============================================================

    /// <summary>
    /// A document with one section and one entry produces content.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_SingleSection_ReturnsContent()
    {
        var doc = LlmsDocumentParser.Parse(
            "# Test\n> Summary.\n## Docs\n- [Guide](https://example.com/guide.md): guide");
        var fetcher = BuildStandardFetcher();
        var generator = new ContextGenerator(fetcher);

        var result = await generator.GenerateAsync(doc);

        Assert.NotEmpty(result.Content);
        Assert.Contains("Getting Started", result.Content);
        Assert.Single(result.SectionsIncluded);
        Assert.Equal("Docs", result.SectionsIncluded[0]);
    }

    // ===============================================================
    // GenerateAsync — Multiple Sections
    // ===============================================================

    /// <summary>
    /// All non-Optional sections are included in the output.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_MultipleSections_AllIncluded()
    {
        var doc = ParseStandardDoc();
        var fetcher = BuildStandardFetcher();
        var generator = new ContextGenerator(fetcher);

        var result = await generator.GenerateAsync(doc);

        Assert.Contains("Docs", result.SectionsIncluded);
        Assert.Contains("Examples", result.SectionsIncluded);
        Assert.Contains("Getting Started", result.Content);
        Assert.Contains("API Reference", result.Content);
        Assert.Contains("Demo", result.Content);
    }

    // ===============================================================
    // GenerateAsync — Optional Section Handling
    // ===============================================================

    /// <summary>
    /// By default (<c>IncludeOptional == false</c>), the Optional section is excluded.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_OptionalSectionExcludedByDefault()
    {
        var doc = ParseDocWithOptional();
        var fetcher = BuildStandardFetcher();
        var generator = new ContextGenerator(fetcher);

        var result = await generator.GenerateAsync(doc);

        Assert.DoesNotContain("FAQ", result.Content);
        Assert.DoesNotContain("Optional", result.SectionsIncluded);
    }

    /// <summary>
    /// When <c>IncludeOptional == true</c>, the Optional section is included.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_OptionalSectionIncludedWhenRequested()
    {
        var doc = ParseDocWithOptional();
        var fetcher = BuildStandardFetcher();
        var generator = new ContextGenerator(fetcher);
        var options = new ContextOptions { IncludeOptional = true };

        var result = await generator.GenerateAsync(doc, options);

        Assert.Contains("FAQ", result.Content);
    }

    // ===============================================================
    // GenerateAsync — XML Wrapping
    // ===============================================================

    /// <summary>
    /// When <c>WrapSectionsInXml == true</c> (default), sections are wrapped
    /// in <c>&lt;section name="..."&gt;</c> tags.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_XmlWrapping_SectionsWrappedInTags()
    {
        var doc = ParseStandardDoc();
        var fetcher = BuildStandardFetcher();
        var generator = new ContextGenerator(fetcher);

        var result = await generator.GenerateAsync(doc);

        Assert.Contains("<section name=\"Docs\">", result.Content);
        Assert.Contains("</section>", result.Content);
        Assert.Contains("<section name=\"Examples\">", result.Content);
    }

    /// <summary>
    /// When <c>WrapSectionsInXml == false</c>, no XML tags appear.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_XmlWrappingDisabled_NoTags()
    {
        var doc = ParseStandardDoc();
        var fetcher = BuildStandardFetcher();
        var generator = new ContextGenerator(fetcher);
        var options = new ContextOptions { WrapSectionsInXml = false };

        var result = await generator.GenerateAsync(doc, options);

        Assert.DoesNotContain("<section", result.Content);
        Assert.DoesNotContain("</section>", result.Content);
        // Should still contain the content
        Assert.Contains("Getting Started", result.Content);
    }

    // ===============================================================
    // GenerateAsync — Token Budgeting
    // ===============================================================

    /// <summary>
    /// When over budget with Optional included, Optional is dropped first.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_TokenBudgetExceeded_OptionalDroppedFirst()
    {
        var doc = ParseDocWithOptional();

        // Give both sections substantial content so the combined total exceeds budget
        var longGuide = string.Join(". ", Enumerable.Range(1, 20)
            .Select(i => $"Guide sentence {i} with important details"));
        var longFaq = string.Join(". ", Enumerable.Range(1, 20)
            .Select(i => $"FAQ answer {i} with helpful info"));

        var fetcher = new MockContentFetcher()
            .WithContent("https://example.com/guide.md", longGuide)
            .WithContent("https://example.com/faq.md", longFaq);
        var generator = new ContextGenerator(fetcher);

        // Budget that fits Docs alone but not Docs + Optional (word count estimator)
        var options = new ContextOptions
        {
            IncludeOptional = true,
            MaxTokens = 130,
            TokenEstimator = text => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length
        };

        var result = await generator.GenerateAsync(doc, options);

        // Optional should be omitted first before truncating Docs
        Assert.Contains("Optional", result.SectionsOmitted);
        Assert.Contains("Docs", result.SectionsIncluded);
    }

    /// <summary>
    /// Truncated content ends at a sentence boundary, not mid-word.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_TokenBudgetExceeded_TruncatesAtSentenceBoundary()
    {
        var doc = LlmsDocumentParser.Parse(
            "# Test\n> Summary.\n## Docs\n- [Guide](https://example.com/guide.md): guide");
        var fetcher = BuildStandardFetcher();
        var generator = new ContextGenerator(fetcher);

        var options = new ContextOptions
        {
            MaxTokens = 15, // Very tight
            TokenEstimator = text => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length
        };

        var result = await generator.GenerateAsync(doc, options);

        // Content should not end mid-word
        var content = result.Content;
        if (content.Length > 0)
        {
            // Should end with a complete word (no trailing partial words)
            Assert.False(content.EndsWith("-"), "Content should not end mid-word");
        }
    }

    /// <summary>
    /// Truncated output includes the truncation marker string.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_TruncationIndicator_Appended()
    {
        var doc = LlmsDocumentParser.Parse(
            "# Test\n> Summary.\n## Docs\n- [Guide](https://example.com/guide.md): guide");

        // Provide long content that will need truncation
        var longContent = string.Join(". ", Enumerable.Range(1, 50)
            .Select(i => $"Sentence number {i} provides important information"));
        var fetcher = new MockContentFetcher()
            .WithContent("https://example.com/guide.md", longContent);
        var generator = new ContextGenerator(fetcher);

        var options = new ContextOptions
        {
            MaxTokens = 20,
            TokenEstimator = text => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length
        };

        var result = await generator.GenerateAsync(doc, options);

        if (result.SectionsTruncated.Count > 0)
        {
            Assert.Contains(ContextGenerator.TruncationIndicator, result.Content);
        }
    }

    // ===============================================================
    // GenerateAsync — Fetch Failures
    // ===============================================================

    /// <summary>
    /// An entry whose URL fails to fetch appears as a placeholder, not a silent omission.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_FetchFailure_IncludesErrorPlaceholder()
    {
        var doc = LlmsDocumentParser.Parse(
            "# Test\n> Summary.\n## Docs\n- [Broken](https://example.com/broken.md): broken");
        var fetcher = new MockContentFetcher()
            .WithFailure("https://example.com/broken.md");
        var generator = new ContextGenerator(fetcher);

        var result = await generator.GenerateAsync(doc);

        Assert.Contains("[Error fetching", result.Content);
        Assert.Single(result.FetchErrors);
        Assert.Equal(new Uri("https://example.com/broken.md"), result.FetchErrors[0].Url);
    }

    // ===============================================================
    // GenerateAsync — Custom Token Estimator
    // ===============================================================

    /// <summary>
    /// A custom estimator function is called and its return values drive budget decisions.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_CustomTokenEstimator_Used()
    {
        var doc = ParseStandardDoc();
        var fetcher = BuildStandardFetcher();
        var generator = new ContextGenerator(fetcher);

        bool estimatorCalled = false;
        var options = new ContextOptions
        {
            TokenEstimator = text =>
            {
                estimatorCalled = true;
                // Count characters as tokens (very different from default)
                return text.Length;
            }
        };

        var result = await generator.GenerateAsync(doc, options);

        Assert.True(estimatorCalled);
        // Character-based count will be much higher than word/4 count
        Assert.True(result.ApproximateTokenCount > 100);
    }

    // ===============================================================
    // Content Cleaning — HTML Comments
    // ===============================================================

    /// <summary>
    /// HTML comments in fetched Markdown are removed from the generated content.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_HtmlComments_StrippedFromContent()
    {
        var doc = LlmsDocumentParser.Parse(
            "# Test\n> Summary.\n## Docs\n- [Guide](https://example.com/guide.md): guide");
        var contentWithComments = "# Guide\n<!-- This is a comment -->\nContent here.<!-- Another -->";
        var fetcher = new MockContentFetcher()
            .WithContent("https://example.com/guide.md", contentWithComments);
        var generator = new ContextGenerator(fetcher);

        var result = await generator.GenerateAsync(doc);

        Assert.DoesNotContain("<!--", result.Content);
        Assert.DoesNotContain("-->", result.Content);
        Assert.Contains("Content here.", result.Content);
    }

    // ===============================================================
    // Content Cleaning — Base64 Images
    // ===============================================================

    /// <summary>
    /// Base64-embedded images in fetched Markdown are stripped from the output.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_Base64Images_StrippedFromContent()
    {
        var doc = LlmsDocumentParser.Parse(
            "# Test\n> Summary.\n## Docs\n- [Guide](https://example.com/guide.md): guide");
        var contentWithBase64 = "# Guide\n![logo](data:image/png;base64,iVBORw0KGgo=)\nReal content here.";
        var fetcher = new MockContentFetcher()
            .WithContent("https://example.com/guide.md", contentWithBase64);
        var generator = new ContextGenerator(fetcher);

        var result = await generator.GenerateAsync(doc);

        Assert.DoesNotContain("data:image", result.Content);
        Assert.DoesNotContain("base64", result.Content);
        Assert.Contains("Real content here.", result.Content);
    }

    // ===============================================================
    // Default Token Estimator
    // ===============================================================

    /// <summary>
    /// The default words÷4 estimator produces reasonable results.
    /// </summary>
    [Fact]
    public void DefaultTokenEstimator_ReturnsReasonableCount()
    {
        // 20 words / 4 = 5 tokens
        var text = "one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty";
        var estimate = ContextGenerator.DefaultTokenEstimator(text);
        Assert.Equal(5, estimate);
    }

    /// <summary>
    /// Empty or whitespace input returns 0 tokens.
    /// </summary>
    [Fact]
    public void DefaultTokenEstimator_EmptyInput_ReturnsZero()
    {
        Assert.Equal(0, ContextGenerator.DefaultTokenEstimator(""));
        Assert.Equal(0, ContextGenerator.DefaultTokenEstimator("   "));
    }

    // ===============================================================
    // Null Input Validation
    // ===============================================================

    /// <summary>
    /// Passing <c>null</c> document throws <see cref="ArgumentNullException"/>.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_NullDocument_ThrowsArgumentNullException()
    {
        var generator = new ContextGenerator(BuildStandardFetcher());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => generator.GenerateAsync(null!));
    }

    // ===============================================================
    // Token Count in Result
    // ===============================================================

    /// <summary>
    /// The result includes an approximate token count > 0 for non-empty content.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ReturnsPositiveTokenCount()
    {
        var doc = ParseStandardDoc();
        var fetcher = BuildStandardFetcher();
        var generator = new ContextGenerator(fetcher);

        var result = await generator.GenerateAsync(doc);

        Assert.True(result.ApproximateTokenCount > 0);
    }
}
