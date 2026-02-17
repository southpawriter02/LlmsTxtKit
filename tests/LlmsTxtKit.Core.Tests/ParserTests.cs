using LlmsTxtKit.Core.Parsing;
using Xunit;

namespace LlmsTxtKit.Core.Tests;

/// <summary>
/// Unit tests for <see cref="LlmsDocumentParser"/>. Covers happy path parsing,
/// edge cases, and malformed input handling — 16 tests total.
/// </summary>
public class ParserTests
{
    // ---------------------------------------------------------------
    // Happy Path
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_ValidDocument_ExtractsTitle()
    {
        const string content = """
            # My Project
            > A short summary.
            ## Docs
            - [Guide](https://example.com/guide.md): The guide
            """;

        var doc = LlmsDocumentParser.Parse(content);

        Assert.Equal("My Project", doc.Title);
    }

    [Fact]
    public void Parse_ValidDocument_ExtractsSummary()
    {
        const string content = """
            # My Project
            > A short summary.
            ## Docs
            - [Guide](https://example.com/guide.md): The guide
            """;

        var doc = LlmsDocumentParser.Parse(content);

        Assert.Equal("A short summary.", doc.Summary);
    }

    [Fact]
    public void Parse_ValidDocument_ExtractsFreeformContent()
    {
        const string content = """
            # FastHTML
            > FastHTML is a python library.
            Important notes:
            - It is not compatible with FastAPI syntax.
            - It works with vanilla JS.
            ## Docs
            - [Quick start](https://example.com/quickstart.md): Overview
            """;

        var doc = LlmsDocumentParser.Parse(content);

        Assert.NotNull(doc.FreeformContent);
        Assert.Contains("Important notes:", doc.FreeformContent);
        Assert.Contains("It is not compatible with FastAPI syntax.", doc.FreeformContent);
    }

    [Fact]
    public void Parse_ValidDocument_ExtractsSections()
    {
        const string content = """
            # My Project
            > Summary.
            ## Docs
            - [Guide](https://example.com/guide.md): The guide
            ## Examples
            - [Demo](https://example.com/demo.md): A demo
            """;

        var doc = LlmsDocumentParser.Parse(content);

        Assert.Equal(2, doc.Sections.Count);
        Assert.Equal("Docs", doc.Sections[0].Name);
        Assert.Equal("Examples", doc.Sections[1].Name);
    }

    [Fact]
    public void Parse_ValidDocument_ExtractsEntries()
    {
        const string content = """
            # My Project
            > Summary.
            ## Docs
            - [Guide](https://example.com/guide.md): The main guide
            - [API Ref](https://example.com/api.md): API reference
            """;

        var doc = LlmsDocumentParser.Parse(content);

        Assert.Equal(2, doc.Sections[0].Entries.Count);

        var first = doc.Sections[0].Entries[0];
        Assert.Equal("Guide", first.Title);
        Assert.Equal(new Uri("https://example.com/guide.md"), first.Url);
        Assert.Equal("The main guide", first.Description);

        var second = doc.Sections[0].Entries[1];
        Assert.Equal("API Ref", second.Title);
        Assert.Equal(new Uri("https://example.com/api.md"), second.Url);
        Assert.Equal("API reference", second.Description);
    }

    [Fact]
    public void Parse_OptionalSection_MarkedCorrectly()
    {
        const string content = """
            # My Project
            > Summary.
            ## Docs
            - [Guide](https://example.com/guide.md): Guide
            ## Optional
            - [Extra](https://example.com/extra.md): Optional resource
            """;

        var doc = LlmsDocumentParser.Parse(content);

        Assert.False(doc.Sections[0].IsOptional);
        Assert.True(doc.Sections[1].IsOptional);
        Assert.Equal("Optional", doc.Sections[1].Name);
    }

    [Fact]
    public void Parse_PreservesRawContent()
    {
        const string content = "# Test\n> Summary.\n## Docs\n- [A](https://example.com/a.md): Note\n";

        var doc = LlmsDocumentParser.Parse(content);

        Assert.Equal(content, doc.RawContent);
    }

    // ---------------------------------------------------------------
    // Edge Cases
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_MinimalDocument_TitleOnly()
    {
        const string content = "# My Project\n";

        var doc = LlmsDocumentParser.Parse(content);

        Assert.Equal("My Project", doc.Title);
        Assert.Null(doc.Summary);
        Assert.Null(doc.FreeformContent);
        Assert.Empty(doc.Sections);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void Parse_EmptySection_HasZeroEntries()
    {
        const string content = """
            # My Project
            > Summary.
            ## Roadmap
            ## Docs
            - [Guide](https://example.com/guide.md): Guide
            """;

        var doc = LlmsDocumentParser.Parse(content);

        Assert.Equal(2, doc.Sections.Count);
        Assert.Equal("Roadmap", doc.Sections[0].Name);
        Assert.Empty(doc.Sections[0].Entries);
        Assert.Single(doc.Sections[1].Entries);
    }

    [Fact]
    public void Parse_EntryWithoutDescription_DescriptionIsNull()
    {
        const string content = """
            # My Project
            > Summary.
            ## Docs
            - [Guide](https://example.com/guide.md)
            """;

        var doc = LlmsDocumentParser.Parse(content);

        var entry = Assert.Single(doc.Sections[0].Entries);
        Assert.Equal("Guide", entry.Title);
        Assert.Null(entry.Description);
    }

    [Fact]
    public void Parse_UnicodeContent_PreservedCorrectly()
    {
        const string content = """
            # Ünïcödé Prøjèct 日本語
            > Diëse Bibliothek unterstützt Unicode — タイトル。
            ## Dökümëntâtïon
            - [Ëinstieg 入門](https://example.com/docs/einführung.md): Schnëllstart — クイックスタート
            """;

        var doc = LlmsDocumentParser.Parse(content);

        Assert.Equal("Ünïcödé Prøjèct 日本語", doc.Title);
        Assert.Contains("タイトル", doc.Summary);
        Assert.Equal("Dökümëntâtïon", doc.Sections[0].Name);
        Assert.Contains("入門", doc.Sections[0].Entries[0].Title);
        Assert.Contains("クイックスタート", doc.Sections[0].Entries[0].Description);
    }

    [Fact]
    public void Parse_FreeformContentIgnoresH3()
    {
        const string content = """
            # My Project
            > Summary.
            ## Docs
            - [Guide](https://example.com/guide.md): Guide
            ### Subsection within Docs
            - [API](https://example.com/api.md): API docs
            """;

        var doc = LlmsDocumentParser.Parse(content);

        // H3 should NOT create a new section — only H2 does
        Assert.Single(doc.Sections);
        Assert.Equal("Docs", doc.Sections[0].Name);
    }

    // ---------------------------------------------------------------
    // Malformed Input
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_NoH1_ProducesErrorDiagnostic()
    {
        const string content = """
            > This file has a summary but no title.
            ## Docs
            - [Guide](https://example.com/guide.md): Guide
            """;

        var doc = LlmsDocumentParser.Parse(content);

        Assert.Equal(string.Empty, doc.Title);
        var error = Assert.Single(doc.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains("No H1 title found", error.Message);
    }

    [Fact]
    public void Parse_MultipleH1_ProducesErrorDiagnostic()
    {
        const string content = """
            # First Title
            > Summary.
            # Second Title
            ## Docs
            - [Guide](https://example.com/guide.md): Guide
            """;

        var doc = LlmsDocumentParser.Parse(content);

        // First H1 should be kept as the title
        Assert.Equal("First Title", doc.Title);
        var error = Assert.Single(doc.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains("Multiple H1 headings", error.Message);
    }

    [Fact]
    public void Parse_NullInput_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => LlmsDocumentParser.Parse(null!));
    }

    [Fact]
    public void Parse_EmptyString_ProducesDiagnostic()
    {
        var doc = LlmsDocumentParser.Parse(string.Empty);

        Assert.Equal(string.Empty, doc.Title);
        Assert.Null(doc.Summary);
        Assert.Empty(doc.Sections);
        var error = Assert.Single(doc.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains("No H1 title found", error.Message);
    }
}
