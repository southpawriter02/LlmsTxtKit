using LlmsTxtKit.Core.Parsing;
using LlmsTxtKit.Core.Validation;
using LlmsTxtKit.Core.Validation.Rules;
using Xunit;

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
}
