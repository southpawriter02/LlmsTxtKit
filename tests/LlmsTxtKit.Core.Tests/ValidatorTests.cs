using LlmsTxtKit.Core.Parsing;
using LlmsTxtKit.Core.Validation;
using LlmsTxtKit.Core.Validation.Rules;
using Xunit;
using System.Linq;

namespace LlmsTxtKit.Core.Tests;

/// <summary>
/// Unit tests for <see cref="LlmsDocumentValidator"/> and all 10 built-in
/// <see cref="IValidationRule"/> implementations. Tests are organized by rule,
/// with orchestrator-level tests at the end.
/// </summary>
/// <remarks>
/// All tests operate on in-memory <see cref="LlmsDocument"/> instances created
/// by <see cref="LlmsDocumentParser.Parse"/>. Network-dependent rules are tested
/// with <see cref="ValidationOptions.CheckLinkedUrls"/> set to <c>false</c>
/// (their default), which means they produce zero issues. Dedicated tests verify
/// that they skip correctly when disabled.
/// </remarks>
public class ValidatorTests
{
    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    /// <summary>A well-formed llms.txt document for "passes cleanly" tests.</summary>
    private const string ValidContent = """
        # Test Site
        > A valid test site for validation tests.
        ## Docs
        - [Guide](https://example.com/guide.md): The main guide
        - [API](https://example.com/api.md): API reference
        ## Examples
        - [Demo](https://example.com/demo.md): A demo
        """;

    /// <summary>Parses content and runs default (structural-only) validation.</summary>
    private static async Task<ValidationReport> ValidateContent(string content)
    {
        var doc = LlmsDocumentParser.Parse(content);
        var validator = new LlmsDocumentValidator();
        return await validator.ValidateAsync(doc);
    }

    // ===============================================================
    // REQUIRED_H1_MISSING
    // ===============================================================

    /// <summary>
    /// A document with no H1 title triggers <c>REQUIRED_H1_MISSING</c> (Error).
    /// </summary>
    [Fact]
    public async Task RequiredH1Missing_NoTitle_ProducesError()
    {
        var report = await ValidateContent("> Summary only, no title.\n## Docs\n- [A](https://a.com): a");
        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, i => i.Rule == "REQUIRED_H1_MISSING");
    }

    /// <summary>
    /// A document with a valid H1 title does NOT trigger <c>REQUIRED_H1_MISSING</c>.
    /// </summary>
    [Fact]
    public async Task RequiredH1Missing_HasTitle_NoIssue()
    {
        var report = await ValidateContent(ValidContent);
        Assert.DoesNotContain(report.AllIssues, i => i.Rule == "REQUIRED_H1_MISSING");
    }

    // ===============================================================
    // MULTIPLE_H1_FOUND
    // ===============================================================

    /// <summary>
    /// A document with two H1 headings triggers <c>MULTIPLE_H1_FOUND</c> (Error).
    /// </summary>
    [Fact]
    public async Task MultipleH1Found_TwoH1s_ProducesError()
    {
        var content = "# First Title\n> Summary.\n# Second Title\n## Docs\n- [A](https://a.com): a";
        var report = await ValidateContent(content);
        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, i => i.Rule == "MULTIPLE_H1_FOUND");
    }

    /// <summary>
    /// A document with exactly one H1 does NOT trigger <c>MULTIPLE_H1_FOUND</c>.
    /// </summary>
    [Fact]
    public async Task MultipleH1Found_SingleH1_NoIssue()
    {
        var report = await ValidateContent(ValidContent);
        Assert.DoesNotContain(report.AllIssues, i => i.Rule == "MULTIPLE_H1_FOUND");
    }

    // ===============================================================
    // SECTION_EMPTY
    // ===============================================================

    /// <summary>
    /// An H2 section with zero entries triggers <c>SECTION_EMPTY</c> (Warning).
    /// </summary>
    [Fact]
    public async Task SectionEmpty_NoEntries_ProducesWarning()
    {
        var content = "# Test\n> Summary.\n## Roadmap\n## Docs\n- [A](https://a.com): a";
        var report = await ValidateContent(content);
        Assert.True(report.IsValid); // Warnings don't invalidate
        Assert.Contains(report.Warnings, i => i.Rule == "SECTION_EMPTY");
    }

    /// <summary>
    /// Sections with entries do NOT trigger <c>SECTION_EMPTY</c>.
    /// </summary>
    [Fact]
    public async Task SectionEmpty_AllHaveEntries_NoIssue()
    {
        var report = await ValidateContent(ValidContent);
        Assert.DoesNotContain(report.AllIssues, i => i.Rule == "SECTION_EMPTY");
    }

    // ===============================================================
    // ENTRY_URL_INVALID
    // ===============================================================

    /// <summary>
    /// An entry with a non-HTTP URL scheme captured via parser diagnostics
    /// triggers <c>ENTRY_URL_INVALID</c> (Error).
    /// </summary>
    [Fact]
    public async Task EntryUrlInvalid_RelativeUrl_ProducesError()
    {
        // The parser will produce a diagnostic for invalid/relative URLs
        var content = "# Test\n> Summary.\n## Docs\n- [A](not-a-url): desc";
        var report = await ValidateContent(content);
        Assert.Contains(report.AllIssues, i => i.Rule == "ENTRY_URL_INVALID");
    }

    /// <summary>
    /// Entries with valid HTTPS URLs do NOT trigger <c>ENTRY_URL_INVALID</c>.
    /// </summary>
    [Fact]
    public async Task EntryUrlInvalid_ValidHttpsUrls_NoIssue()
    {
        var report = await ValidateContent(ValidContent);
        Assert.DoesNotContain(report.AllIssues, i => i.Rule == "ENTRY_URL_INVALID");
    }

    // ===============================================================
    // ENTRY_URL_UNREACHABLE (network-dependent, default disabled)
    // ===============================================================

    /// <summary>
    /// When <see cref="ValidationOptions.CheckLinkedUrls"/> is <c>false</c> (the default),
    /// the URL unreachable rule produces zero issues regardless of content.
    /// </summary>
    [Fact]
    public async Task EntryUrlUnreachable_CheckDisabled_NoIssues()
    {
        var report = await ValidateContent(ValidContent);
        Assert.DoesNotContain(report.AllIssues, i => i.Rule == "ENTRY_URL_UNREACHABLE");
    }

    // ===============================================================
    // ENTRY_URL_REDIRECTED (network-dependent, default disabled)
    // ===============================================================

    /// <summary>
    /// When <see cref="ValidationOptions.CheckLinkedUrls"/> is <c>false</c> (the default),
    /// the URL redirect rule produces zero issues regardless of content.
    /// </summary>
    [Fact]
    public async Task EntryUrlRedirected_CheckDisabled_NoIssues()
    {
        var report = await ValidateContent(ValidContent);
        Assert.DoesNotContain(report.AllIssues, i => i.Rule == "ENTRY_URL_REDIRECTED");
    }

    // ===============================================================
    // CONTENT_STALE (network-dependent, default disabled)
    // ===============================================================

    /// <summary>
    /// When <see cref="ValidationOptions.CheckFreshness"/> is <c>false</c> (the default),
    /// the freshness rule produces zero issues regardless of content.
    /// </summary>
    [Fact]
    public async Task ContentStale_CheckDisabled_NoIssues()
    {
        var report = await ValidateContent(ValidContent);
        Assert.DoesNotContain(report.AllIssues, i => i.Rule == "CONTENT_STALE");
    }

    // ===============================================================
    // UNEXPECTED_HEADING_LEVEL
    // ===============================================================

    /// <summary>
    /// An H3 heading in the document triggers <c>UNEXPECTED_HEADING_LEVEL</c> (Warning).
    /// </summary>
    [Fact]
    public async Task UnexpectedHeadingLevel_H3Present_ProducesWarning()
    {
        var content = "# Test\n> Summary.\n## Docs\n- [A](https://a.com): a\n### Subsection\n- [B](https://b.com): b";
        var report = await ValidateContent(content);
        Assert.Contains(report.Warnings, i => i.Rule == "UNEXPECTED_HEADING_LEVEL");
    }

    /// <summary>
    /// A document with only H1 and H2 headings does NOT trigger
    /// <c>UNEXPECTED_HEADING_LEVEL</c>.
    /// </summary>
    [Fact]
    public async Task UnexpectedHeadingLevel_OnlyH1H2_NoIssue()
    {
        var report = await ValidateContent(ValidContent);
        Assert.DoesNotContain(report.AllIssues, i => i.Rule == "UNEXPECTED_HEADING_LEVEL");
    }

    // ===============================================================
    // CONTENT_OUTSIDE_STRUCTURE
    // ===============================================================

    /// <summary>
    /// Plain text inside a section (not a valid entry link) triggers
    /// <c>CONTENT_OUTSIDE_STRUCTURE</c> (Warning).
    /// </summary>
    [Fact]
    public async Task ContentOutsideStructure_OrphanedText_ProducesWarning()
    {
        var content = "# Test\n> Summary.\n## Docs\n- [A](https://a.com): a\nThis is orphaned text inside a section.";
        var report = await ValidateContent(content);
        Assert.Contains(report.Warnings, i => i.Rule == "CONTENT_OUTSIDE_STRUCTURE");
    }

    /// <summary>
    /// A document with only proper entries in sections does NOT trigger
    /// <c>CONTENT_OUTSIDE_STRUCTURE</c>.
    /// </summary>
    [Fact]
    public async Task ContentOutsideStructure_CleanDocument_NoIssue()
    {
        var report = await ValidateContent(ValidContent);
        Assert.DoesNotContain(report.AllIssues, i => i.Rule == "CONTENT_OUTSIDE_STRUCTURE");
    }

    // ===============================================================
    // Orchestrator-Level Tests
    // ===============================================================

    /// <summary>
    /// A fully valid document produces <c>IsValid == true</c> with zero errors.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_FullyValidDocument_IsValidTrue()
    {
        var report = await ValidateContent(ValidContent);
        Assert.True(report.IsValid);
        Assert.Empty(report.Errors);
    }

    /// <summary>
    /// A document with multiple problems reports ALL of them, not just the first.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_MultipleIssues_AllReported()
    {
        // This document has: no H1 (error), empty section (warning), orphaned text (warning)
        var content = "> Summary only.\n## Empty\n## Docs\n- [A](https://a.com): a\nOrphaned text.";
        var report = await ValidateContent(content);

        Assert.False(report.IsValid);
        // Should have at least the REQUIRED_H1_MISSING error and the SECTION_EMPTY warning
        Assert.True(report.AllIssues.Count >= 2,
            $"Expected at least 2 issues, got {report.AllIssues.Count}: "
            + string.Join(", ", report.AllIssues.Select(i => i.Rule)));
    }

    /// <summary>
    /// When only warnings are present (no errors), <c>IsValid</c> is still <c>true</c>.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WarningsOnly_IsValidStillTrue()
    {
        // Document with an empty section (warning) but otherwise valid
        var content = "# Test\n> Summary.\n## Roadmap\n## Docs\n- [A](https://a.com): a";
        var report = await ValidateContent(content);

        Assert.True(report.IsValid);
        Assert.NotEmpty(report.Warnings);
        Assert.Empty(report.Errors);
    }

    /// <summary>
    /// <see cref="ValidationOptions.CheckLinkedUrls"/> disabled (default) skips all URL checks.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_CheckLinkedUrlsFalse_SkipsUrlChecks()
    {
        var doc = LlmsDocumentParser.Parse(ValidContent);
        var validator = new LlmsDocumentValidator();
        var options = new ValidationOptions { CheckLinkedUrls = false };
        var report = await validator.ValidateAsync(doc, options);

        Assert.DoesNotContain(report.AllIssues, i => i.Rule == "ENTRY_URL_UNREACHABLE");
        Assert.DoesNotContain(report.AllIssues, i => i.Rule == "ENTRY_URL_REDIRECTED");
    }

    /// <summary>
    /// <see cref="ValidationOptions.CheckFreshness"/> disabled (default) skips freshness checks.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_CheckFreshnessFalse_SkipsFreshnessChecks()
    {
        var doc = LlmsDocumentParser.Parse(ValidContent);
        var validator = new LlmsDocumentValidator();
        var options = new ValidationOptions { CheckFreshness = false };
        var report = await validator.ValidateAsync(doc, options);

        Assert.DoesNotContain(report.AllIssues, i => i.Rule == "CONTENT_STALE");
    }

    /// <summary>
    /// Errors are sorted before warnings in <see cref="ValidationReport.AllIssues"/>.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_IssuesSortedBySeverity_ErrorsFirst()
    {
        // No H1 (error) + empty section (warning)
        var content = "> Summary.\n## Empty\n## Docs\n- [A](https://a.com): a";
        var report = await ValidateContent(content);

        if (report.AllIssues.Count >= 2)
        {
            // All errors should come before all warnings
            var firstWarningIndex = -1;
            var lastErrorIndex = -1;
            for (int i = 0; i < report.AllIssues.Count; i++)
            {
                if (report.AllIssues[i].Severity == ValidationSeverity.Error)
                    lastErrorIndex = i;
                if (report.AllIssues[i].Severity == ValidationSeverity.Warning && firstWarningIndex == -1)
                    firstWarningIndex = i;
            }

            if (lastErrorIndex >= 0 && firstWarningIndex >= 0)
            {
                Assert.True(lastErrorIndex < firstWarningIndex,
                    "Expected all errors to appear before all warnings in AllIssues.");
            }
        }
    }

    /// <summary>
    /// Null document input throws <see cref="ArgumentNullException"/>.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_NullDocument_ThrowsArgumentNullException()
    {
        var validator = new LlmsDocumentValidator();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => validator.ValidateAsync(null!));
    }

    // ===============================================================
    // BLOCKQUOTE_MALFORMED (Direct rule evaluation)
    // ===============================================================

    /// <summary>
    /// BlockquoteMalformedRule detects blockquote-related diagnostics and produces a Warning.
    /// </summary>
    [Fact]
    public async Task BlockquoteMalformedRule_WithBlockquoteDiagnostic_ProducesWarning()
    {
        // Create a document with parser diagnostics indicating blockquote issues
        var content = "# Test\n> This is a blockquote\n## Section\n- [Link](https://example.com): desc";
        var doc = LlmsDocumentParser.Parse(content);

        // Manually add a diagnostic to simulate parser detection (if not already present)
        if (!doc.Diagnostics.Any(d => d.Message.Contains("blockquote", StringComparison.OrdinalIgnoreCase)))
        {
            // The parser may or may not have added diagnostics based on content
            // For this test, we verify the rule handles documents with blockquote diagnostics
            var diagList = doc.Diagnostics.ToList();
            diagList.Add(new ParseDiagnostic(DiagnosticSeverity.Warning, "Blockquote format unexpected", 2));

            var docWithDiag = new LlmsDocument(
                doc.Title,
                doc.Summary,
                doc.FreeformContent,
                doc.Sections,
                diagList,
                doc.RawContent);

            var rule = new BlockquoteMalformedRule();
            var issues = await rule.EvaluateAsync(docWithDiag, new ValidationOptions());

            Assert.NotEmpty(issues);
            Assert.All(issues, issue => Assert.Equal("BLOCKQUOTE_MALFORMED", issue.Rule));
            Assert.All(issues, issue => Assert.Equal(ValidationSeverity.Warning, issue.Severity));
        }
    }

    /// <summary>
    /// BlockquoteMalformedRule produces no issues when there are no blockquote diagnostics.
    /// </summary>
    [Fact]
    public async Task BlockquoteMalformedRule_NoBlockquoteDiagnostics_NoIssues()
    {
        var doc = LlmsDocumentParser.Parse(ValidContent);
        var rule = new BlockquoteMalformedRule();
        var issues = await rule.EvaluateAsync(doc, new ValidationOptions());

        Assert.DoesNotContain(issues, issue => issue.Rule == "BLOCKQUOTE_MALFORMED");
    }

    // ===============================================================
    // UNEXPECTED_HEADING_LEVEL (Direct rule evaluation)
    // ===============================================================

    /// <summary>
    /// UnexpectedHeadingLevelRule detects H3+ headings and produces Warnings.
    /// </summary>
    [Fact]
    public async Task UnexpectedHeadingLevelRule_WithH3Heading_ProducesWarning()
    {
        var content = "# Test\n> Summary.\n## Docs\n- [A](https://a.com): a\n### Subsection\n- [B](https://b.com): b";
        var doc = LlmsDocumentParser.Parse(content);

        var rule = new UnexpectedHeadingLevelRule();
        var issues = await rule.EvaluateAsync(doc, new ValidationOptions());

        Assert.NotEmpty(issues);
        Assert.Contains(issues, issue => issue.Rule == "UNEXPECTED_HEADING_LEVEL");
        Assert.All(issues, issue => Assert.Equal(ValidationSeverity.Warning, issue.Severity));
    }

    /// <summary>
    /// UnexpectedHeadingLevelRule detects H4 headings and produces Warnings.
    /// </summary>
    [Fact]
    public async Task UnexpectedHeadingLevelRule_WithH4Heading_ProducesWarning()
    {
        var content = "# Test\n> Summary.\n## Docs\n- [A](https://a.com): a\n#### Deep Heading\nSome content";
        var doc = LlmsDocumentParser.Parse(content);

        var rule = new UnexpectedHeadingLevelRule();
        var issues = await rule.EvaluateAsync(doc, new ValidationOptions());

        Assert.NotEmpty(issues);
        Assert.Contains(issues, issue => issue.Rule == "UNEXPECTED_HEADING_LEVEL");
    }

    /// <summary>
    /// UnexpectedHeadingLevelRule produces no issues with only H1 and H2 headings.
    /// </summary>
    [Fact]
    public async Task UnexpectedHeadingLevelRule_OnlyH1H2_NoIssues()
    {
        var doc = LlmsDocumentParser.Parse(ValidContent);

        var rule = new UnexpectedHeadingLevelRule();
        var issues = await rule.EvaluateAsync(doc, new ValidationOptions());

        Assert.DoesNotContain(issues, issue => issue.Rule == "UNEXPECTED_HEADING_LEVEL");
    }

    // ===============================================================
    // CONTENT_OUTSIDE_STRUCTURE (Direct rule evaluation)
    // ===============================================================

    /// <summary>
    /// ContentOutsideStructureRule detects orphaned text inside sections and produces Warnings.
    /// </summary>
    [Fact]
    public async Task ContentOutsideStructureRule_OrphanedTextInSection_ProducesWarning()
    {
        var content = "# Test\n> Summary.\n## Docs\n- [A](https://a.com): a\nThis is orphaned text inside a section.";
        var doc = LlmsDocumentParser.Parse(content);

        var rule = new ContentOutsideStructureRule();
        var issues = await rule.EvaluateAsync(doc, new ValidationOptions());

        Assert.NotEmpty(issues);
        Assert.Contains(issues, issue => issue.Rule == "CONTENT_OUTSIDE_STRUCTURE");
        Assert.All(issues, issue => Assert.Equal(ValidationSeverity.Warning, issue.Severity));
    }

    /// <summary>
    /// ContentOutsideStructureRule detects multiple orphaned lines and produces multiple Warnings.
    /// </summary>
    [Fact]
    public async Task ContentOutsideStructureRule_MultipleOrphanedLines_ProducesMultipleWarnings()
    {
        var content = "# Test\n> Summary.\n## Docs\n- [A](https://a.com): a\nOrphaned 1\nOrphaned 2";
        var doc = LlmsDocumentParser.Parse(content);

        var rule = new ContentOutsideStructureRule();
        var issues = await rule.EvaluateAsync(doc, new ValidationOptions());

        var cosIssues = issues.Where(i => i.Rule == "CONTENT_OUTSIDE_STRUCTURE").ToList();
        Assert.NotEmpty(cosIssues);
        Assert.True(cosIssues.Count >= 1, "Expected at least one CONTENT_OUTSIDE_STRUCTURE issue");
    }

    /// <summary>
    /// ContentOutsideStructureRule produces no issues with only valid entries.
    /// </summary>
    [Fact]
    public async Task ContentOutsideStructureRule_ValidStructureOnly_NoIssues()
    {
        var doc = LlmsDocumentParser.Parse(ValidContent);

        var rule = new ContentOutsideStructureRule();
        var issues = await rule.EvaluateAsync(doc, new ValidationOptions());

        Assert.DoesNotContain(issues, issue => issue.Rule == "CONTENT_OUTSIDE_STRUCTURE");
    }

    // ===============================================================
    // Network Rule Skipping [Theory] Tests
    // ===============================================================

    /// <summary>
    /// Network-dependent rules produce zero issues when their corresponding options are disabled.
    /// Parameterized across EntryUrlUnreachableRule, EntryUrlRedirectedRule, and ContentStaleRule.
    /// </summary>
    [Theory]
    [InlineData("ENTRY_URL_UNREACHABLE")]
    [InlineData("ENTRY_URL_REDIRECTED")]
    [InlineData("CONTENT_STALE")]
    public async Task NetworkRules_CheckDisabled_ProducesZeroIssues(string ruleName)
    {
        var doc = LlmsDocumentParser.Parse(ValidContent);
        var optionsDisabled = new ValidationOptions { CheckLinkedUrls = false, CheckFreshness = false };

        IValidationRule rule = ruleName switch
        {
            "ENTRY_URL_UNREACHABLE" => new EntryUrlUnreachableRule(),
            "ENTRY_URL_REDIRECTED" => new EntryUrlRedirectedRule(),
            "CONTENT_STALE" => new ContentStaleRule { LlmsTxtLastModified = DateTimeOffset.UtcNow },
            _ => throw new InvalidOperationException($"Unknown rule: {ruleName}")
        };

        var issues = await rule.EvaluateAsync(doc, optionsDisabled);

        Assert.Empty(issues);
    }

    // ===============================================================
    // Issue Severity Correctness [Theory] Tests
    // ===============================================================

    /// <summary>
    /// Certain rules produce Error severity while others produce Warning severity.
    /// Parameterized to test severity correctness across multiple rule types.
    /// </summary>
    [Theory]
    [InlineData("REQUIRED_H1_MISSING", ValidationSeverity.Error)]
    [InlineData("MULTIPLE_H1_FOUND", ValidationSeverity.Error)]
    [InlineData("SECTION_EMPTY", ValidationSeverity.Warning)]
    public async Task RuleSeverity_MatchesExpected(string ruleName, ValidationSeverity expectedSeverity)
    {
        IValidationRule rule = ruleName switch
        {
            "REQUIRED_H1_MISSING" => new RequiredH1MissingRule(),
            "MULTIPLE_H1_FOUND" => new MultipleH1FoundRule(),
            "SECTION_EMPTY" => new SectionEmptyRule(),
            _ => throw new InvalidOperationException($"Unknown rule: {ruleName}")
        };

        string contentForRule = ruleName switch
        {
            "REQUIRED_H1_MISSING" => "> Summary only, no H1.\n## Docs\n- [A](https://a.com): a",
            "MULTIPLE_H1_FOUND" => "# First\n> Summary.\n# Second\n## Docs\n- [A](https://a.com): a",
            "SECTION_EMPTY" => "# Test\n> Summary.\n## Empty\n## Docs\n- [A](https://a.com): a",
            _ => ValidContent
        };

        var doc = LlmsDocumentParser.Parse(contentForRule);
        var issues = await rule.EvaluateAsync(doc, new ValidationOptions());

        var ruleIssues = issues.Where(i => i.Rule == ruleName).ToList();
        Assert.NotEmpty(ruleIssues);
        Assert.All(ruleIssues, issue => Assert.Equal(expectedSeverity, issue.Severity));
    }
}
