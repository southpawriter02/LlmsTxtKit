# LlmsTxtKit

**A C#/.NET library and MCP server for llms.txt-aware content retrieval, parsing, validation, and caching.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-8.0+-purple.svg)](https://dotnet.microsoft.com/)
<!-- Uncomment when packages are published:
[![NuGet: Core](https://img.shields.io/nuget/v/LlmsTxtKit.Core.svg)](https://www.nuget.org/packages/LlmsTxtKit.Core)
[![NuGet: Mcp](https://img.shields.io/nuget/v/LlmsTxtKit.Mcp.svg)](https://www.nuget.org/packages/LlmsTxtKit.Mcp)
-->

---

## What This Is

LlmsTxtKit is the first (and currently only) .NET implementation of tooling for the [`/llms.txt` standard](https://llmstxt.org/) — a convention proposed by Jeremy Howard of [Answer.AI](https://answer.ai) that helps AI systems discover and retrieve clean, curated Markdown content from websites. The library provides everything a .NET developer needs to work with llms.txt files programmatically: parsing them into strongly-typed objects, fetching them with infrastructure-aware retry logic, validating them against the spec, caching results to avoid hammering origin servers, and generating LLM-ready context strings from their contents.

It also ships as an MCP (Model Context Protocol) server, which means any MCP-capable AI agent — Claude, GitHub Copilot, or any custom agent built on the MCP standard — can use LlmsTxtKit as a tool to discover and retrieve llms.txt-curated content at inference time. This is significant because it implements the real-time retrieval workflow that the llms.txt spec was designed for but that no major LLM platform has built natively.

---

## Why This Exists

### The .NET Ecosystem Gap

Every existing llms.txt tool is written in Python or JavaScript. The official [`llms_txt` Python module](https://github.com/AnswerDotAI/llms-txt) handles parsing and context generation. JavaScript equivalents mirror it. Docusaurus and VitePress plugins generate llms.txt files from documentation sites. There are even implementations in PHP and Drupal.

But for .NET developers — the people building on ASP.NET Core, Azure, Blazor, and the broader Microsoft ecosystem — there is nothing. Zero packages on NuGet. No parser. No validator. No MCP server. If you're building an AI-integrated .NET application and you want to work with llms.txt, you have to write everything from scratch or shell out to a Python process. LlmsTxtKit fills that gap with a native, idiomatic C# implementation that follows .NET conventions, uses proper dependency injection, and ships with XML documentation on every public type and method.

### The Infrastructure Problem

The llms.txt spec sounds simple: fetch a Markdown file from a known URL, parse it, follow the links. In practice, the web's security infrastructure makes this surprisingly difficult. Roughly 20% of public websites sit behind Cloudflare, which began blocking AI crawlers by default on new domains in July 2025. AI-style HTTP requests — those without JavaScript execution, without cookies, from data center IPs, with non-browser user agents — trigger Web Application Firewall (WAF) heuristics that return 403 Forbidden responses instead of content.

LlmsTxtKit is built with this reality in mind. Its fetching layer doesn't just make HTTP requests and hope for the best. It produces structured `FetchResult` objects that distinguish between "file found," "file not found," "blocked by WAF," "rate limited," "DNS failure," "timeout," and other outcomes. Calling code gets the information it needs to make intelligent decisions about fallback strategies, and the library's caching layer ensures that when a fetch *does* succeed, the result is preserved so you're not fighting the same WAF battle on every request.

### The Trust Gap

One of the most underappreciated obstacles to llms.txt adoption at the platform level is the lack of trust mechanisms. Because llms.txt content is maintained separately from the HTML it describes, there's no built-in way to verify that the Markdown matches the live page, or to detect when it's gone stale. Google's John Mueller compared llms.txt to the discredited keywords meta tag for exactly this reason — without validation, it's self-reported metadata with no accountability.

LlmsTxtKit's validation system addresses this directly. The `ValidationReport` checks structural compliance with the spec, verifies that linked URLs actually resolve, and optionally inspects Last-Modified headers and content hashes to flag freshness concerns. It doesn't solve the trust problem completely — that would require platform-level adoption of verification mechanisms — but it gives developers and AI agents a structured way to assess confidence in llms.txt content before using it. This is a step beyond what any existing tool provides, and it's designed so that future trust mechanisms (content signatures, registry verification, etc.) can be added as the ecosystem matures.

---

## Packages

LlmsTxtKit ships as two distinct NuGet packages. You can use either one independently or together.

### LlmsTxtKit.Core

The standalone library. Use this when you want to work with llms.txt files directly in your own .NET application — parsing, fetching, validating, caching, and generating context. No MCP dependency. No server infrastructure. Just a library you call from your code.

### LlmsTxtKit.Mcp

The MCP server. Use this when you want to expose llms.txt capabilities as tools that AI agents can call. It wraps LlmsTxtKit.Core and serves it over the Model Context Protocol, so any MCP-capable AI system can discover, fetch, validate, and retrieve llms.txt-curated content from any website.

---

## Quick Start

### Installation

```bash
# Core library only — for direct use in your .NET applications
dotnet add package LlmsTxtKit.Core

# MCP server — for exposing llms.txt tools to AI agents
dotnet add package LlmsTxtKit.Mcp
```

> **Prerequisite:** .NET 8.0 SDK or later. LlmsTxtKit targets `net8.0` and uses modern C# language features including nullable reference types and required properties.

### Parsing an llms.txt File

The parser reads raw llms.txt content and produces a strongly-typed `LlmsDocument` object. This object gives you structured access to the title, summary, sections, and file lists described in the spec, without needing to write any Markdown parsing logic yourself.

```csharp
using LlmsTxtKit.Core.Parsing;

// Parse from a string (useful when you've already fetched the content yourself)
string rawContent = await File.ReadAllTextAsync("example-llms.txt");
LlmsDocument document = LlmsDocumentParser.Parse(rawContent);

// The document object gives you typed access to every part of the spec:
Console.WriteLine($"Title: {document.Title}");
Console.WriteLine($"Summary: {document.Summary}");

// Sections are the H2-delimited groups of links described in the spec.
// Each section contains a list of entries, where each entry has a URL,
// a description, and metadata about whether it's in the Optional section.
foreach (LlmsSection section in document.Sections)
{
    Console.WriteLine($"\nSection: {section.Name} ({section.Entries.Count} entries)");
    
    foreach (LlmsEntry entry in section.Entries)
    {
        Console.WriteLine($"  - {entry.Description}");
        Console.WriteLine($"    URL: {entry.Url}");
    }
}
```

### Fetching with Infrastructure Awareness

The fetcher handles the real-world complexity of retrieving llms.txt files from the open web — WAF blocking, rate limiting, timeouts, redirect chains, and DNS failures. Instead of throwing exceptions on non-success HTTP responses, it returns structured `FetchResult` objects that tell you exactly what happened so your code can respond intelligently.

```csharp
using LlmsTxtKit.Core.Fetching;

// Create a fetcher with sensible defaults for timeout, retry, and user-agent.
// The user-agent is configurable because some WAFs block generic user agents
// but allow identified tool-specific ones.
var fetcher = new LlmsTxtFetcher(new FetcherOptions
{
    UserAgent = "LlmsTxtKit/1.0 (https://github.com/southpawriter02/LlmsTxtKit)",
    TimeoutSeconds = 15,
    MaxRetries = 2
});

FetchResult result = await fetcher.FetchAsync("https://docs.anthropic.com");

// FetchResult.Status tells you what happened at the infrastructure level,
// not just whether you got a 200 OK. This is critical because a 403 from
// Cloudflare means something very different than a 404 or a DNS failure.
switch (result.Status)
{
    case FetchStatus.Success:
        // result.Document contains the parsed LlmsDocument
        Console.WriteLine($"Found: {result.Document!.Title}");
        Console.WriteLine($"Sections: {result.Document.Sections.Count}");
        break;
    
    case FetchStatus.NotFound:
        // The server responded normally but there's no llms.txt file.
        // This is the expected outcome for most websites.
        Console.WriteLine("No llms.txt file at this domain.");
        break;
    
    case FetchStatus.Blocked:
        // A WAF, CDN, or bot-detection system blocked the request.
        // result.BlockReason may contain details (e.g., "Cloudflare 403",
        // "challenge page detected").
        Console.WriteLine($"Blocked: {result.BlockReason}");
        break;
    
    case FetchStatus.RateLimited:
        // The server returned 429 Too Many Requests.
        // result.RetryAfter may contain a suggested wait duration.
        Console.WriteLine($"Rate limited. Retry after: {result.RetryAfter}");
        break;
    
    case FetchStatus.DnsFailure:
    case FetchStatus.Timeout:
    case FetchStatus.Error:
        // Network-level failures. result.ErrorMessage has details.
        Console.WriteLine($"Fetch failed: {result.ErrorMessage}");
        break;
}
```

### Validating Against the Spec

The validator checks whether a parsed `LlmsDocument` conforms to the llms.txt specification and whether the content it references is actually accessible and current. The `ValidationReport` it produces contains structured warnings and errors, not just a pass/fail boolean — so you can decide your own tolerance thresholds.

```csharp
using LlmsTxtKit.Core.Validation;

// Validate a document that was already parsed (from a fetch or from a string)
var validator = new LlmsDocumentValidator();
ValidationReport report = await validator.ValidateAsync(document, new ValidationOptions
{
    // Check that every URL listed in the llms.txt file actually resolves
    // to a live page. Disabled by default because it's slow (one HTTP
    // request per link), but valuable for thorough audits.
    CheckLinkedUrls = true,
    
    // Compare Last-Modified headers between the llms.txt file and the
    // pages it links to. Flags potential staleness if the HTML has been
    // updated more recently than the Markdown version.
    CheckFreshness = true
});

Console.WriteLine($"Valid: {report.IsValid}");
Console.WriteLine($"Warnings: {report.Warnings.Count}");
Console.WriteLine($"Errors: {report.Errors.Count}");

// Each issue includes a severity, a rule identifier, a human-readable
// message, and the location in the document where the issue was found.
foreach (ValidationIssue issue in report.AllIssues)
{
    Console.WriteLine($"  [{issue.Severity}] {issue.Rule}: {issue.Message}");
}
```

### Generating LLM-Ready Context

The context generator takes an `LlmsDocument` and expands it into a single string suitable for injection into an LLM prompt. It follows the same approach used by FastHTML's `llms-ctx.txt` generator — fetching linked Markdown files, wrapping them in XML-style section markers, and concatenating them into a coherent context block. The key addition is token budgeting: you can specify a maximum token count, and the generator will prioritize required sections over optional ones, truncating gracefully rather than cutting off mid-sentence.

```csharp
using LlmsTxtKit.Core.Context;

var generator = new ContextGenerator();
string context = await generator.GenerateAsync(document, new ContextOptions
{
    // Maximum approximate token count for the generated context.
    // If the full expansion exceeds this, Optional sections are dropped
    // first, then remaining sections are truncated with an indicator.
    MaxTokens = 8000,
    
    // Whether to include the Optional section content. The llms.txt spec
    // says Optional sections should be skipped "if you are short on space."
    // Setting this to false always skips them regardless of token budget.
    IncludeOptional = true,
    
    // Whether to wrap each section's content in XML-style tags like
    // <section name="Docs">...</section>. This helps LLMs understand
    // document structure and attribute information to the correct source.
    WrapSectionsInXml = true
});

// The resulting string is ready to be prepended to an LLM prompt as
// reference context. It contains the site's curated documentation in
// a clean, structured format that LLMs handle significantly better
// than raw HTML.
Console.WriteLine($"Generated context: {context.Length} characters");
```

### Running the MCP Server

The MCP server exposes LlmsTxtKit.Core as a set of tools that any MCP-capable AI agent can call. Once running, agents can discover llms.txt files, fetch specific sections, run validation, generate context, and compare HTML versus Markdown versions of the same content — all through structured tool calls.

```bash
# Run the MCP server as a standalone process
dotnet run --project src/LlmsTxtKit.Mcp

# Or, if installed as a global tool:
dotnet tool install --global LlmsTxtKit.Mcp
llmstxtkit-mcp --port 5050
```

To connect the server to Claude Desktop, add it to your MCP configuration file (typically `claude_desktop_config.json`):

```json
{
    "mcpServers": {
        "llmstxtkit": {
            "command": "dotnet",
            "args": ["run", "--project", "/path/to/LlmsTxtKit.Mcp"],
            "env": {}
        }
    }
}
```

Once connected, the AI agent has access to the following tools:

---

## MCP Tools Reference

The MCP server exposes five tools. Each one maps directly to a capability in LlmsTxtKit.Core but is designed for the way AI agents work — structured input parameters, structured output with clear status indicators, and graceful handling of infrastructure failures.

### `llmstxt_discover`

**Purpose:** Check whether a website has an llms.txt file and, if so, return its parsed structure.

This is typically the first tool an agent calls when it needs information from a website. Rather than immediately fetching the full content (which could be expensive for sites with many linked pages), discovery returns just the document's skeleton — title, summary, and the list of sections with their entry counts — so the agent can decide which sections are relevant before fetching anything else.

**Input Parameters:**

| Parameter | Type | Required | Description |
|---|---|---|---|
| `domain` | `string` | Yes | The domain to check (e.g., `docs.anthropic.com`). The tool constructs the full URL (`https://{domain}/llms.txt`) automatically. |
| `follow_redirects` | `boolean` | No | Whether to follow HTTP redirects (default: `true`). Some sites redirect from the root domain to a docs subdomain. |

**Output:** A JSON object containing the `FetchStatus` (success, not found, blocked, etc.), and if successful, the parsed document structure including title, summary, section names, and entry counts per section. If the fetch was blocked, the output includes the `BlockReason` to help the agent (or user) understand what happened.

### `llmstxt_fetch_section`

**Purpose:** Fetch the full content of one or more sections from a site's llms.txt document.

Once an agent has discovered a site's llms.txt structure via `llmstxt_discover`, it can use this tool to retrieve the actual content of specific sections. This is more efficient than generating the full context when the agent only needs documentation for one feature or API endpoint.

**Input Parameters:**

| Parameter | Type | Required | Description |
|---|---|---|---|
| `domain` | `string` | Yes | The domain whose llms.txt to read from. |
| `sections` | `string[]` | Yes | Section names to fetch (e.g., `["Docs", "Examples"]`). Must match the H2 headings in the llms.txt file. |
| `max_tokens` | `integer` | No | Maximum approximate token count for the returned content. If the combined section content exceeds this, entries are prioritized by order of appearance and truncated gracefully. |

**Output:** A JSON object containing the fetched content for each requested section, with per-section status indicators (some sections may succeed while others fail if individual linked URLs are unreachable). Content is returned as clean Markdown text, optionally wrapped in XML section markers.

### `llmstxt_validate`

**Purpose:** Run a full validation check on a site's llms.txt file and report any spec violations, broken links, or freshness concerns.

This tool is useful for site owners auditing their own llms.txt files, for AI agents assessing confidence in content before using it, and for research purposes (the benchmark study uses it to filter the test corpus to sites with well-formed files).

**Input Parameters:**

| Parameter | Type | Required | Description |
|---|---|---|---|
| `domain` | `string` | Yes | The domain to validate. |
| `check_linked_urls` | `boolean` | No | Whether to verify that every linked URL resolves (default: `false` — it's slow but thorough). |
| `check_freshness` | `boolean` | No | Whether to compare Last-Modified headers between the llms.txt file and linked pages (default: `false`). |

**Output:** The full `ValidationReport` as a JSON object, including a top-level `IsValid` boolean, arrays of warnings and errors (each with severity, rule ID, message, and document location), and freshness metadata if requested.

### `llmstxt_context`

**Purpose:** Generate a complete, LLM-ready context string from a site's llms.txt document.

This is the "give me everything" tool — it fetches the llms.txt file, expands all linked Markdown content, and assembles it into a single context block suitable for injection into an LLM prompt. It's equivalent to what FastHTML's `llms-ctx-full.txt` provides, but generated on demand with fresh content and configurable token budgets.

**Input Parameters:**

| Parameter | Type | Required | Description |
|---|---|---|---|
| `domain` | `string` | Yes | The domain to generate context for. |
| `max_tokens` | `integer` | No | Maximum approximate token count. Optional sections are dropped first if the budget is exceeded. |
| `include_optional` | `boolean` | No | Whether to include the Optional section content (default: `true`). |
| `wrap_xml` | `boolean` | No | Whether to wrap sections in XML-style tags (default: `true`). |

**Output:** A JSON object containing the generated context string, the total approximate token count, a list of which sections were included and which were dropped (if any), and the fetch/validation status for each linked resource.

### `llmstxt_compare`

**Purpose:** For a given URL, fetch both the HTML page and the llms.txt-linked Markdown version (if available) and report on the differences.

This tool was specifically designed to support the [Context Collapse Mitigation Benchmark](https://github.com/southpawriter02/llmstxt-research/tree/main/benchmark), but it's broadly useful for anyone comparing what an AI system would see when reading a page via standard web retrieval versus via llms.txt-curated Markdown. The comparison includes token count differences, content overlap estimation, and freshness deltas.

**Input Parameters:**

| Parameter | Type | Required | Description |
|---|---|---|---|
| `url` | `string` | Yes | The URL of the HTML page to compare. The tool looks up the site's llms.txt to find whether this URL has a corresponding Markdown version. |
| `strip_html` | `boolean` | No | Whether to strip HTML tags from the HTML version before comparison (default: `true`). This simulates how most retrieval pipelines process HTML before passing it to an LLM. |

**Output:** A JSON object containing both versions of the content (HTML-derived text and Markdown), token counts for each, a content overlap score, Last-Modified timestamps for both versions (if available), and a `has_markdown_version` flag indicating whether the site's llms.txt links to a Markdown equivalent of the requested page.

---

## Documentation-First Development

LlmsTxtKit follows a strict documentation-first methodology. Before any code is written, the following specification documents are produced and reviewed. They live in the [`specs/`](specs/) directory and are versioned alongside the code, so you can trace every implementation decision back to the design rationale that motivated it.

**Product Requirements Specification** ([`specs/prs.md`](specs/prs.md)) — Defines the problem being solved, the target users (library consumers and MCP agent consumers), success criteria, explicit non-goals (what the project deliberately does *not* do), and the constraints that shaped the design. If you want to understand *why* LlmsTxtKit exists and *who* it's for, start here.

**Design Specification** ([`specs/design-spec.md`](specs/design-spec.md)) — The technical architecture document. Component responsibilities, data flow diagrams, dependency choices, error handling strategy, caching semantics, and rationale for every significant design decision. If you want to understand *how* the library works at an architectural level before reading the code, this is the document.

**User Stories** ([`specs/user-stories.md`](specs/user-stories.md)) — Concrete usage scenarios for both audiences: .NET developers integrating the library directly, and AI agents using the MCP server. Each story follows the "As a [role], I want [capability] so that [benefit]" format and maps to specific API surface area. These stories are what the test plan validates.

**Test Plan** ([`specs/test-plan.md`](specs/test-plan.md)) — Coverage targets, the testing strategy for each component (unit tests, integration tests with mock HTTP servers, MCP protocol compliance tests), the test data corpus, and the specific real-world scenarios that integration tests simulate (WAF blocking, rate limiting, redirect chains, malformed content, etc.).

---

## Testing

LlmsTxtKit uses [xUnit](https://xunit.net/) for testing. The test suite is organized by component and includes both unit tests (isolated logic verification) and integration tests (simulated real-world HTTP scenarios).

### Running Tests

```bash
# Run the full test suite
dotnet test

# Run only Core library tests
dotnet test tests/LlmsTxtKit.Core.Tests

# Run only MCP server tests
dotnet test tests/LlmsTxtKit.Mcp.Tests

# Run with verbose output and code coverage
dotnet test --verbosity normal --collect:"XPlat Code Coverage"
```

### Test Data Corpus

The [`tests/TestData/`](tests/TestData/) directory contains a curated corpus of real-world and synthetic llms.txt files organized into three categories:

**`valid/`** — Well-formed llms.txt files sourced from production websites (Anthropic, Cloudflare, Stripe, FastHTML, Vercel, and others). These verify that the parser handles real production content correctly, not just spec-perfect examples. Each file includes a comment header noting its source URL and the date it was captured.

**`malformed/`** — Intentionally broken files that test parser resilience. These cover common authoring mistakes discovered through surveying real implementations: missing H1 titles, blockquote summaries with incorrect formatting, H2 sections containing plain text instead of link lists, relative URLs instead of absolute URLs, mixed Markdown link styles, and files that use non-standard section headers.

**`edge-cases/`** — Files that are technically valid but push unusual boundaries: Unicode content in titles and descriptions, very large files (1000+ entries), empty files (just a title and nothing else), files with only Optional sections, files with deeply nested Markdown in descriptions, and files with URL-encoded characters in links.

---

## Repository Structure

```
LlmsTxtKit/
├── README.md                         ← You are here
├── CONTRIBUTING.md                   # Development setup, PR guidelines, coding standards
├── CHANGELOG.md                      # Version history (Keep a Changelog format)
├── LICENSE                           # MIT
├── LlmsTxtKit.sln                    # Solution file
│
├── src/
│   ├── LlmsTxtKit.Core/
│   │   ├── LlmsTxtKit.Core.csproj
│   │   ├── Parsing/                  # LlmsDocument model, parser, spec-compliant tokenization
│   │   ├── Fetching/                 # HTTP client, FetchResult types, retry logic, UA config
│   │   ├── Validation/               # ValidationReport, rule engine, freshness checks
│   │   ├── Caching/                  # In-memory + file-backed cache, TTL management
│   │   └── Context/                  # Context generation, section expansion, token budgeting
│   └── LlmsTxtKit.Mcp/
│       ├── LlmsTxtKit.Mcp.csproj
│       ├── Tools/                    # MCP tool definitions (discover, fetch, validate, etc.)
│       ├── Server/                   # MCP protocol handling, transport, registration
│       └── Program.cs                # Entry point, configuration, DI setup
│
├── tests/
│   ├── LlmsTxtKit.Core.Tests/
│   │   ├── ParserTests.cs            # Spec-compliant and edge-case parsing
│   │   ├── ValidatorTests.cs         # Per-rule validation testing
│   │   ├── FetcherTests.cs           # Mock HTTP scenarios (WAF block, 404, redirects, etc.)
│   │   ├── CacheTests.cs             # TTL, eviction, serialization
│   │   └── ContextTests.cs           # Context generation, Optional section handling
│   ├── LlmsTxtKit.Mcp.Tests/
│   │   └── ToolResponseTests.cs      # MCP protocol compliance
│   └── TestData/
│       ├── valid/                    # Corpus of well-formed llms.txt files from real sites
│       ├── malformed/                # Intentionally broken files for parser resilience testing
│       └── edge-cases/               # Unicode, very large files, empty files, etc.
│
├── docs/
│   ├── api/                          # Generated API reference from XML doc comments
│   └── images/                       # Architecture diagrams, data flow illustrations
│
└── specs/
    ├── prs.md                        # Product Requirements Specification
    ├── design-spec.md                # Architecture, components, data flow, decision rationale
    ├── user-stories.md               # Library consumer + MCP agent consumer stories
    └── test-plan.md                  # Coverage targets, integration scenarios, test data corpus
```

---

## Relationship to the Research Initiative

LlmsTxtKit is the practical implementation arm of a broader research initiative documented in the [llmstxt-research](https://github.com/southpawriter02/llmstxt-research) repository. That repository contains an analytical paper examining the infrastructure contradictions preventing llms.txt from fulfilling its design intent ("The llms.txt Access Paradox"), an empirical benchmark measuring whether llms.txt-curated content actually improves AI response quality ("Context Collapse Mitigation Benchmark"), and a blog series translating the findings into practitioner guidance.

The connections between the repositories are direct and concrete. The paper documents the Access Paradox — LlmsTxtKit implements workarounds for it (the `FetchResult` status codes, the WAF-aware retry logic, the graceful degradation). The paper identifies the trust gap — LlmsTxtKit's `ValidationReport` is a first step toward addressing it. The benchmark study uses LlmsTxtKit's `llmstxt_compare` tool to generate the paired HTML-vs-Markdown data that forms its experimental input. If you're interested in the *why* behind LlmsTxtKit's design decisions, the research repository has the evidence that motivated them.

That said, LlmsTxtKit is designed to be useful on its own merits, entirely independently of the research. You don't need to read the paper to use the library. It's a practical tool for a practical problem: .NET developers need a way to work with llms.txt files, and this is the package that does it.

For the full cross-project delivery schedule, tech stack rationale, maintenance commitments, and ongoing coordination strategy, see the **[Unified Roadmap](https://github.com/southpawriter02/llmstxt-research/blob/main/ROADMAP.md)** in the research repository.

---

## Roadmap

LlmsTxtKit is under active development. The following roadmap reflects the current plan, but priorities may shift based on community feedback and the findings from the benchmark study.

### v1.0 (Target: ~8 weeks from project start)

The initial release focuses on spec-complete llms.txt support with robust infrastructure handling. All five Core components (Parsing, Fetching, Validation, Caching, Context Generation) and all five MCP tools are included. Documentation-first: the PRS, Design Spec, User Stories, and Test Plan are complete *before* implementation begins.

### Post-v1.0 (Tentative)

**Content Signals integration.** Cloudflare's Content Signals framework provides structured metadata about a page's content type, audience, and freshness — information that could augment llms.txt validation and trust scoring. If the benchmark study shows that content quality metadata improves retrieval outcomes, a Content Signals adapter would be a natural extension.

**Registry integration.** If a community-maintained llms.txt registry emerges (tracking which sites have files, their validation status, and freshness), LlmsTxtKit could integrate with it to provide pre-validated lookups instead of per-request fetching. This would reduce latency and improve reliability, particularly for high-traffic AI agent use cases.

**Broader spec coverage.** The llms.txt ecosystem is still evolving. If the spec is updated, or if competing standards (CC Signals, IETF aipref) gain traction, LlmsTxtKit may extend to support them — keeping the same architectural pattern of parse → fetch → validate → cache → generate context.

---

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for development setup, coding standards, and pull request guidelines.

The most impactful contributions right now are:

**Test data.** Real-world llms.txt files — especially ones that expose parser edge cases, unusual section structures, or non-standard Markdown — help improve parser resilience. If you've found an llms.txt file that doesn't parse cleanly, please submit it as a test case.

**Infrastructure scenarios.** If you've encountered interesting WAF blocking behavior, rate limiting patterns, or redirect chains when fetching llms.txt files, we'd like to add those scenarios to the integration test suite. The more real-world HTTP behavior the fetcher can handle gracefully, the more reliable it is for everyone.

**Spec interpretation questions.** The llms.txt spec is intentionally minimal, which means there are ambiguities (How should parsers handle relative URLs? What if a section has entries *and* free text? What constitutes a "valid" Markdown link in this context?). If you encounter an ambiguity, filing an issue helps us document the interpretation decisions and track where the spec could be clearer.

---

## Acknowledgments

The `/llms.txt` standard was created by [Jeremy Howard](https://jeremy.fast.ai/) and the [Answer.AI](https://answer.ai) team. The specification is maintained at [llmstxt.org](https://llmstxt.org/) and the reference Python implementation is at [github.com/AnswerDotAI/llms-txt](https://github.com/AnswerDotAI/llms-txt). LlmsTxtKit is a community-driven .NET implementation that builds on their work.

The MCP (Model Context Protocol) standard was developed by [Anthropic](https://anthropic.com/). LlmsTxtKit.Mcp implements the MCP server specification as documented at [modelcontextprotocol.io](https://modelcontextprotocol.io/).

---

## License

LlmsTxtKit is licensed under the [MIT License](https://opensource.org/licenses/MIT). See [LICENSE](LICENSE) for the full text.
