# Design Specification

**Author:** Ryan
**Version:** 1.0
**Last Updated:** February 2026
**Status:** Draft
**Prerequisites:** Read the [PRS](prs.md) first — this document assumes familiarity with the problem statement, target users, success criteria, and non-goals.

---

## 1. Architecture Overview

LlmsTxtKit is structured as two NuGet packages with a clear dependency direction:

```
┌─────────────────────────────────────────────────────┐
│  LlmsTxtKit.Mcp                                    │
│  (MCP Server — standalone process)                  │
│                                                     │
│  ┌─────────┐ ┌──────────────┐ ┌──────────────────┐ │
│  │ Tools/  │ │ Server/      │ │ Program.cs       │ │
│  │ (5 MCP  │ │ (Protocol    │ │ (Entry point,    │ │
│  │  tools) │ │  handling,   │ │  DI setup,       │ │
│  │         │ │  transport)  │ │  configuration)  │ │
│  └────┬────┘ └──────┬───────┘ └────────┬─────────┘ │
│       │             │                   │           │
│       └─────────────┼───────────────────┘           │
│                     │ depends on                    │
└─────────────────────┼───────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────────┐
│  LlmsTxtKit.Core                                    │
│  (Standalone library — no MCP dependency)            │
│                                                     │
│  ┌──────────┐ ┌──────────┐ ┌────────────┐          │
│  │ Parsing/ │ │Fetching/ │ │Validation/ │          │
│  │          │ │          │ │            │          │
│  │ Parser   │ │ Fetcher  │ │ Validator  │          │
│  │ LlmsDoc  │ │ FetchRes │ │ ValReport  │          │
│  └────┬─────┘ └────┬─────┘ └─────┬──────┘          │
│       │            │              │                 │
│  ┌────┴─────┐ ┌────┴────────────┬┘                 │
│  │ Caching/ │ │ Context/        │                   │
│  │          │ │                 │                   │
│  │ Cache    │ │ ContextGen      │                   │
│  │ CacheEnt │ │ ContextOpts     │                   │
│  └──────────┘ └─────────────────┘                   │
└─────────────────────────────────────────────────────┘
```

**Key design principle:** LlmsTxtKit.Core is completely independent. It has no knowledge of MCP, no transport-layer concerns, and no dependency on LlmsTxtKit.Mcp. A developer can use Core without ever installing or running the MCP server. The Mcp package depends on Core, never the reverse.

---

## 2. Component Responsibilities

### 2.1 Parsing (LlmsTxtKit.Core.Parsing)

**Purpose:** Convert raw llms.txt Markdown content into a strongly-typed `LlmsDocument` object model.

**Key types:**

```
LlmsDocument
├── Title: string                    // Required. The H1 content.
├── Summary: string?                 // Optional. The blockquote content.
├── FreeformContent: string?         // Optional. Markdown between summary and first H2.
├── Sections: IReadOnlyList<LlmsSection>
│   ├── Name: string                 // The H2 heading text.
│   ├── IsOptional: bool             // True if this section is titled "Optional".
│   └── Entries: IReadOnlyList<LlmsEntry>
│       ├── Url: Uri                 // The link target.
│       ├── Title: string?           // The link text.
│       └── Description: string?     // Text after the colon, if present.
└── RawContent: string               // The original unparsed content (preserved for debugging).
```

**Design decisions:**

- **Immutable types.** `LlmsDocument`, `LlmsSection`, and `LlmsEntry` are immutable once constructed. The parser produces them; consumers read them. This prevents accidental mutation and makes the types safe for caching and concurrent access.

- **Lenient parsing with structured errors.** The parser does not throw on malformed input. Instead, it returns an `LlmsDocument` with whatever it could extract, plus a `ParseDiagnostics` collection that records warnings (non-fatal issues like a missing blockquote) and errors (structural problems like no H1 title). This lets callers decide their own strictness threshold rather than having the parser enforce one.

- **No Markdown library dependency for the happy path.** The llms.txt spec uses a deliberately restricted subset of Markdown (H1, H2, blockquotes, lists with links). The parser handles this subset with targeted string processing rather than pulling in a full Markdown AST library (like Markdig). This keeps the dependency tree minimal and follows the same approach as the reference implementation (`miniparse.py` is ~20 lines of regex-based parsing). However, Markdig may be used as an *optional* dependency for the context generator (Section 2.5), which needs to process arbitrary Markdown from linked pages.

- **Behavioral alignment with the reference parser.** The reference Python implementation (`AnswerDotAI/llms-txt`, specifically `miniparse.py`) defines the canonical parsing behavior. Key behavioral details derived from the reference code: (1) sections are delimited by `^##\s*` — only H2 headings create new sections; H3+ headings are treated as content within the parent section; (2) the blockquote summary captures a single line only (`^>\s*(?P<summary>.+?$)`); (3) the "Optional" section check is case-sensitive and exact-match (`k != 'Optional'`); (4) the canonical link pattern is `-\s*\[(?P<title>[^\]]+)\]\((?P<url>[^\)]+)\)(?::\s*(?P<desc>.*))?`; (5) the freeform content between the blockquote and first H2 is captured as a single blob (the "info" field). LlmsTxtKit's parser must produce equivalent results for all well-formed inputs, while adding structured diagnostics for malformed inputs (which the reference parser does not handle).

**Public API surface:**

```csharp
namespace LlmsTxtKit.Core.Parsing;

/// <summary>
/// Parses raw llms.txt content into a strongly-typed <see cref="LlmsDocument"/>.
/// </summary>
public static class LlmsDocumentParser
{
    /// <summary>
    /// Parses the provided raw llms.txt content string.
    /// </summary>
    /// <param name="content">The raw Markdown content of an llms.txt file.</param>
    /// <returns>
    /// A parsed <see cref="LlmsDocument"/> containing the extracted structure and
    /// any <see cref="ParseDiagnostic"/> entries for issues encountered during parsing.
    /// </returns>
    public static LlmsDocument Parse(string content);
}
```

### 2.2 Fetching (LlmsTxtKit.Core.Fetching)

**Purpose:** Retrieve llms.txt files from the open web with infrastructure-aware error handling.

**Key types:**

```
FetchResult
├── Status: FetchStatus              // Enum: Success, NotFound, Blocked, RateLimited,
│                                    //        DnsFailure, Timeout, Error
├── Document: LlmsDocument?          // Populated only on Success.
├── RawContent: string?              // The raw response body (available on Success).
├── StatusCode: int?                 // HTTP status code (null on DNS/timeout failures).
├── ResponseHeaders: Dictionary?     // HTTP response headers (for cache validation, etc.).
├── BlockReason: string?             // Human-readable WAF block diagnosis (on Blocked).
├── RetryAfter: TimeSpan?            // From 429 Retry-After header (on RateLimited).
├── ErrorMessage: string?            // Diagnostic message (on DnsFailure, Timeout, Error).
└── Duration: TimeSpan               // How long the fetch took (for performance monitoring).

FetcherOptions
├── UserAgent: string                // Configurable UA string. Default identifies LlmsTxtKit.
├── TimeoutSeconds: int              // Per-request timeout. Default: 15.
├── MaxRetries: int                  // Retry count for transient failures. Default: 2.
├── RetryDelayMs: int                // Base delay between retries. Default: 1000.
└── AcceptHeaderOverride: string?    // Override Accept header if needed.
```

**Design decisions:**

- **FetchStatus enum over exceptions.** The fetcher never throws on non-success HTTP responses. A 403 from Cloudflare is not an exceptional condition when fetching llms.txt files — it's a *common, expected outcome*. The `FetchStatus` enum gives callers structured information they can match on without try/catch boilerplate. Exceptions are reserved for truly exceptional conditions (e.g., `HttpClient` disposal during a request).

- **WAF block detection heuristics.** A 403 response from Cloudflare often includes identifiable response headers (`cf-ray`, `cf-mitigated`, specific `server: cloudflare` header) and response body patterns (Cloudflare challenge pages). The fetcher inspects these signals to populate `BlockReason` with a more useful diagnosis than just "403 Forbidden." This is best-effort — not all WAFs produce identifiable responses — but even imperfect diagnosis is better than none.

- **IHttpClientFactory integration.** The fetcher accepts an `HttpClient` via constructor injection, allowing callers to use `IHttpClientFactory` for proper lifecycle management (connection pooling, handler rotation). For simple usage, a no-argument constructor creates an internal client with default settings.

**Public API surface:**

```csharp
namespace LlmsTxtKit.Core.Fetching;

/// <summary>
/// Fetches llms.txt files from the web with infrastructure-aware error handling.
/// </summary>
public class LlmsTxtFetcher
{
    /// <param name="options">Configuration for timeouts, retries, and user-agent.</param>
    /// <param name="httpClient">
    /// Optional <see cref="HttpClient"/> instance. If null, an internal client
    /// is created. Prefer supplying a client from <see cref="IHttpClientFactory"/>
    /// for proper connection lifecycle management.
    /// </param>
    public LlmsTxtFetcher(FetcherOptions? options = null, HttpClient? httpClient = null);

    /// <summary>
    /// Fetches and parses the llms.txt file for the specified domain.
    /// </summary>
    /// <param name="domain">
    /// The domain to fetch from. The fetcher constructs the URL as
    /// <c>https://{domain}/llms.txt</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for async cancellation.</param>
    /// <returns>A <see cref="FetchResult"/> describing the outcome.</returns>
    public Task<FetchResult> FetchAsync(string domain, CancellationToken cancellationToken = default);
}
```

### 2.3 Validation (LlmsTxtKit.Core.Validation)

**Purpose:** Check whether a parsed `LlmsDocument` conforms to the spec and whether its referenced content is accessible and current.

**Key types:**

```
ValidationReport
├── IsValid: bool                    // True if zero errors (warnings don't invalidate).
├── Errors: IReadOnlyList<ValidationIssue>
├── Warnings: IReadOnlyList<ValidationIssue>
└── AllIssues: IReadOnlyList<ValidationIssue>  // Convenience: Errors + Warnings, sorted.

ValidationIssue
├── Severity: ValidationSeverity     // Enum: Error, Warning
├── Rule: string                     // Machine-readable rule ID (e.g., "REQUIRED_H1_MISSING").
├── Message: string                  // Human-readable description.
└── Location: string?                // Where in the document (line number or section name).

ValidationOptions
├── CheckLinkedUrls: bool            // HTTP HEAD each linked URL. Default: false (slow).
├── CheckFreshness: bool             // Compare Last-Modified headers. Default: false.
└── UrlCheckTimeout: int             // Timeout per URL check in seconds. Default: 10.
```

**Validation rules:**

| Rule ID | Severity | Description |
|---|---|---|
| `REQUIRED_H1_MISSING` | Error | No H1 title found. |
| `MULTIPLE_H1_FOUND` | Error | More than one H1 heading. |
| `BLOCKQUOTE_MALFORMED` | Warning | Blockquote present but doesn't follow expected format. |
| `SECTION_EMPTY` | Warning | H2 section has no entries. |
| `ENTRY_URL_INVALID` | Error | An entry contains a URL that doesn't parse as a valid URI. |
| `ENTRY_URL_UNREACHABLE` | Warning | Linked URL returned non-200 (only when `CheckLinkedUrls` is true). |
| `ENTRY_URL_REDIRECTED` | Warning | Linked URL redirects (may indicate stale reference). |
| `CONTENT_STALE` | Warning | llms.txt is older than the HTML it links to (only when `CheckFreshness` is true). |
| `UNEXPECTED_HEADING_LEVEL` | Warning | Headings other than H1 and H2 found (spec only defines these two). |
| `CONTENT_OUTSIDE_STRUCTURE` | Warning | Content that doesn't belong to any defined structural element. |

**Design decisions:**

- **Rule-based architecture.** Each validation rule is an independent class implementing `IValidationRule`. This makes rules individually testable, individually configurable, and independently extensible. Adding a new rule is additive — it doesn't touch existing rule logic.

- **Warnings vs. errors.** The distinction matters. Errors mean the document is structurally invalid per the spec (missing required H1, invalid URLs). Warnings mean something is suspicious but not spec-violating (empty sections, stale content, redirecting URLs). `IsValid` reflects only errors, not warnings. Callers choose their own warning tolerance.

### 2.4 Caching (LlmsTxtKit.Core.Caching)

**Purpose:** Store and retrieve parsed documents to avoid redundant fetches.

**Key types:**

```
CacheEntry
├── Document: LlmsDocument           // The parsed document.
├── ValidationReport: ValidationReport?  // Most recent validation (may be null if not validated).
├── FetchedAt: DateTimeOffset         // When the content was last fetched.
├── ExpiresAt: DateTimeOffset         // When the TTL expires.
├── HttpHeaders: Dictionary?          // ETag, Last-Modified, etc. for conditional requests.
└── FetchResult: FetchResult          // The full fetch result (including status, duration).

CacheOptions
├── DefaultTtl: TimeSpan             // How long entries live. Default: 1 hour.
├── MaxEntries: int                  // Maximum cached domains. Default: 1000.
├── StaleWhileRevalidate: bool       // Serve stale entries while refreshing. Default: true.
└── BackingStore: ICacheBackingStore? // Null = in-memory only. Set for file-backed persistence.
```

**Design decisions:**

- **Domain-keyed.** Cache entries are keyed by domain (not by full URL), because each domain has exactly one `/llms.txt` path. This simplifies the cache API: `Get("anthropic.com")` rather than `Get("https://docs.anthropic.com/llms.txt")`.

- **Stale-while-revalidate pattern.** When a cached entry's TTL has expired, the cache can return the stale entry immediately while triggering an async background refresh. This is opt-in (via `StaleWhileRevalidate`). For AI inference use cases, returning stale content immediately is often better than blocking on a re-fetch that might fail or be slow.

- **ICacheBackingStore abstraction.** The default is in-memory only (a `ConcurrentDictionary`). For applications that need persistence across process restarts, `ICacheBackingStore` defines a simple `SaveAsync`/`LoadAsync` interface. A file-backed implementation is provided. The abstraction also allows Redis-backed or database-backed implementations without changing Core.

### 2.5 Context Generation (LlmsTxtKit.Core.Context)

**Purpose:** Expand an `LlmsDocument` into an LLM-ready context string by fetching and concatenating linked Markdown content.

**Key types:**

```
ContextOptions
├── MaxTokens: int?                  // Approximate token budget. Null = no limit.
├── IncludeOptional: bool            // Include ## Optional section content. Default: false.
│                                    // (Aligns with reference implementation's create_ctx(optional=False).)
├── WrapSectionsInXml: bool          // Wrap sections in <section name="...">. Default: true.
└── TokenEstimator: Func<string, int>?  // Custom token counting function. Default: word/4 heuristic.
```

**Design decisions:**

- **Token budgeting is approximate by default.** The default token estimator uses a words÷4 heuristic, which is reasonably accurate for English text across most tokenizers. Callers who need precise counts for a specific model (GPT-4, Claude, Llama, etc.) can supply their own `TokenEstimator` function. The generator respects whatever function is provided. This avoids shipping model-specific tokenizer dependencies (see PRS Non-Goal NG-6).

- **Graceful truncation.** When the token budget is exceeded, the generator follows a priority order: first, the Optional section is dropped entirely (this matches the spec's semantics for the Optional section). If still over budget, remaining sections are truncated at sentence boundaries (not mid-word, not mid-sentence). A truncation indicator (`[... content truncated to fit token budget ...]`) is appended so downstream consumers know the context is incomplete.

- **Fetching linked content.** The context generator uses `LlmsTxtFetcher` internally to retrieve the Markdown content of each linked URL. This means the same infrastructure-aware fetching (WAF handling, timeouts, retries) applies to linked content, not just the llms.txt file itself. Links that fail to fetch are included as a placeholder noting the failure, not silently omitted. Following the reference implementation's behavior, HTML comments and base64-embedded images are stripped from fetched Markdown content before inclusion.

- **XML format divergence from reference implementation.** The reference Python implementation generates XML using fastcore's FT (FastTags) system with a `<project title="..." summary="...">` root element, semantic section elements named after the H2 heading (e.g., `<docs>`, `<examples>`), and `<doc title="..." url="...">` wrappers for each linked document's content. LlmsTxtKit uses a different approach: generic `<section name="...">` wrappers with string content. This is a deliberate design choice — the generic wrapper approach avoids generating arbitrary XML element names from user content (which could produce invalid XML if section names contain special characters) and provides a more predictable schema for downstream consumers. Both formats convey the same structural information.

- **Parallel fetching consideration.** The reference implementation supports parallel document fetching via a `n_workers` parameter using threadpool-based concurrency. LlmsTxtKit v1.0 fetches linked documents sequentially. Parallel fetching using `Task.WhenAll` with configurable concurrency limits is a candidate for post-v1.0 enhancement.

---

## 3. MCP Tool Definitions (LlmsTxtKit.Mcp.Tools)

The MCP server exposes five tools. Each tool delegates to LlmsTxtKit.Core services. The Mcp layer is a thin protocol adapter — it translates MCP tool invocations into Core method calls and formats Core return types into MCP response structures.

### 3.1 llmstxt_discover

**Description:** Given a domain, checks for `/llms.txt` and returns the parsed document structure.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `domain` | string | Yes | The domain to check (e.g., "docs.anthropic.com"). |

**Returns:** JSON object containing `found` (boolean), `status` (FetchStatus string), and if found: `title`, `summary`, `sections` (array of section names with entry counts), and `diagnostics` (any parse warnings/errors).

**Implementation:** Calls `LlmsTxtFetcher.FetchAsync(domain)`. If Success, returns the parsed structure. If NotFound/Blocked/Error, returns the status with diagnostic information.

### 3.2 llmstxt_fetch_section

**Description:** Given a domain and section name, fetches and returns the content of all linked resources in that section.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `domain` | string | Yes | The domain to fetch from. |
| `section` | string | Yes | The section name (H2 heading text, e.g., "Docs"). |

**Returns:** JSON object containing the section's entries with their fetched Markdown content (or error information for entries that failed to fetch).

**Implementation:** Calls `FetchAsync` for the llms.txt file (or retrieves from cache), locates the named section, then fetches each linked URL's content.

### 3.3 llmstxt_validate

**Description:** Runs full validation on a site's llms.txt and returns the validation report.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `domain` | string | Yes | The domain to validate. |
| `check_urls` | boolean | No | Whether to verify linked URLs resolve. Default: false. |
| `check_freshness` | boolean | No | Whether to compare Last-Modified headers. Default: false. |

**Returns:** JSON object containing `is_valid`, `error_count`, `warning_count`, and an array of issues (each with severity, rule, message, and location).

### 3.4 llmstxt_context

**Description:** Generates the full LLM-ready context for a given site.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `domain` | string | Yes | The domain to generate context for. |
| `max_tokens` | integer | No | Approximate token budget. Null = no limit. |
| `include_optional` | boolean | No | Whether to include Optional section. Default: false (matches reference implementation). |

**Returns:** JSON object containing the generated context string, actual token count (approximate), sections included, and any sections that were truncated or omitted.

### 3.5 llmstxt_compare

**Description:** Given a URL, fetches both the HTML page and the llms.txt-linked Markdown version (if available) and reports differences.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `url` | string | Yes | The specific page URL to compare. |

**Returns:** JSON object containing `html_size` (bytes), `markdown_size` (bytes), `size_ratio`, `html_token_estimate`, `markdown_token_estimate`, `token_ratio`, `freshness_delta` (Last-Modified comparison if available), and `markdown_available` (whether a .md version was found).

**Implementation:** This tool is designed specifically to support the Benchmark study's data collection. It fetches the HTML page normally, then checks for a corresponding Markdown version at `{url}.md` (following the llms.txt convention). Both are measured for size and approximate token count. The comparison data feeds directly into the benchmark's Condition A / Condition B design.

---

## 4. Data Flow — Request Lifecycle

The following traces a typical request through the system, from an MCP tool invocation to a returned result. This shows how the components interact.

**Scenario:** An AI agent calls `llmstxt_context` for `docs.anthropic.com` with a 4000-token budget.

```
1. MCP Server receives tool invocation:
   { tool: "llmstxt_context", params: { domain: "docs.anthropic.com", max_tokens: 4000 } }

2. Tools/ContextTool extracts parameters, constructs ContextOptions.

3. ContextTool checks Cache for "docs.anthropic.com":
   a. Cache HIT (not expired) → skip to step 5 with cached LlmsDocument.
   b. Cache HIT (expired, stale-while-revalidate) → return stale, trigger async refresh.
   c. Cache MISS → proceed to step 4.

4. ContextTool calls Fetcher.FetchAsync("docs.anthropic.com"):
   a. Fetcher constructs URL: https://docs.anthropic.com/llms.txt
   b. Fetcher sends HTTP GET with configured UA, timeout, and headers.
   c. Response received. Fetcher classifies the result:
      - 200 OK → FetchStatus.Success. Raw content passed to Parser.
      - 403 + Cloudflare headers → FetchStatus.Blocked. BlockReason populated.
      - 404 → FetchStatus.NotFound.
      - 429 + Retry-After → FetchStatus.RateLimited.
      - Timeout → FetchStatus.Timeout.
      - DNS error → FetchStatus.DnsFailure.
   d. On Success: Parser.Parse(rawContent) produces LlmsDocument.
   e. FetchResult (with Document) stored in Cache.

5. ContextGenerator.GenerateAsync(document, options):
   a. Iterates over document.Sections.
   b. For each section (respecting IncludeOptional):
      - For each entry in the section:
        - Fetcher.FetchAsync(entry.Url) to retrieve linked Markdown content.
        - (Failed fetches produce placeholder error markers.)
      - If WrapSectionsInXml: wraps in <section name="...">...</section>.
   c. Concatenates all section content.
   d. Applies token budgeting:
      - If total exceeds MaxTokens and Optional sections exist: drop Optional first.
      - If still over: truncate at sentence boundaries with indicator.
   e. Returns generated context string.

6. ContextTool formats result as MCP response JSON.

7. MCP Server returns response to agent.
```

---

## 5. Error Handling Strategy

LlmsTxtKit uses a **structured results** approach rather than exception-heavy control flow.

**Core principle:** Expected failure modes return structured result types. Unexpected failures throw exceptions.

| Scenario | Handling |
|---|---|
| HTTP 403, 404, 429, 5xx | `FetchResult.Status` enum — no exception. |
| DNS failure, timeout | `FetchResult.Status` enum — no exception. |
| Malformed llms.txt content | `ParseDiagnostics` collection on `LlmsDocument` — no exception. |
| Validation rule violations | `ValidationReport.Errors` and `.Warnings` — no exception. |
| Null/empty input to parser | `ArgumentException` — this is a programming error, not a runtime condition. |
| `HttpClient` disposed during fetch | `ObjectDisposedException` — unrecoverable; propagated. |
| Out-of-memory during large file parse | `OutOfMemoryException` — unrecoverable; propagated. |

---

## 6. Configuration Model

All configuration uses **options objects** passed to constructors or method parameters. There are no global settings, no static configuration, and no ambient state.

| Component | Options Type | Key Settings |
|---|---|---|
| Fetcher | `FetcherOptions` | UserAgent, TimeoutSeconds, MaxRetries, RetryDelayMs |
| Validator | `ValidationOptions` | CheckLinkedUrls, CheckFreshness, UrlCheckTimeout |
| Cache | `CacheOptions` | DefaultTtl, MaxEntries, StaleWhileRevalidate, BackingStore |
| Context Generator | `ContextOptions` | MaxTokens, IncludeOptional, WrapSectionsInXml, TokenEstimator |

The MCP server reads its configuration from environment variables and/or a `config.json` file, then translates them into the appropriate Core options objects. This keeps Core configuration-source-agnostic.

---

## 7. Dependency Choices

| Dependency | Used By | Purpose | Rationale |
|---|---|---|---|
| `System.Net.Http` (built-in) | Fetching | HTTP requests | Standard .NET HTTP abstraction. No third-party HTTP library needed. |
| `System.Text.Json` (built-in) | Mcp, Caching | JSON serialization | Ships with .NET 8. Avoids Newtonsoft.Json dependency for new projects. |
| `Markdig` (optional) | Context | Markdown processing | Only needed if context generator processes complex Markdown from linked pages. Listed as optional dependency — Core's parser does not require it. |
| `xUnit` (test-only) | Tests | Test framework | De facto .NET test standard. See CONTRIBUTING.md. |
| `coverlet` (test-only) | Tests | Code coverage | Standard .NET coverage collector. |

**Design principle: minimal production dependencies.** LlmsTxtKit.Core should have zero third-party NuGet dependencies for its core functionality (parsing, fetching, validation, caching). The only potential external dependency is Markdig for the context generator, and even that is isolated behind an interface so it can be swapped or omitted.

---

## 8. Security Considerations

**SEC-1: No credential storage or transmission.** LlmsTxtKit does not handle authentication, API keys, or credentials. It makes unauthenticated HTTP GET requests to publicly-accessible URLs.

**SEC-2: No WAF circumvention.** The fetcher identifies itself honestly via its User-Agent string. It does not spoof browser user agents, execute JavaScript, or attempt to bypass bot detection. See PRS Non-Goal NG-5.

**SEC-3: Input validation.** The parser handles arbitrary string input without crashing. Extremely large inputs (multi-megabyte "llms.txt" files that are clearly not legitimate) are rejected with a configurable size limit to prevent memory exhaustion.

**SEC-4: URL validation.** All URLs extracted from llms.txt entries are validated as well-formed URIs before any fetch attempt. Relative URLs, `javascript:` URLs, `data:` URLs, and other non-HTTP(S) schemes are rejected.

**SEC-5: No code execution.** The parser processes Markdown text. It does not execute embedded scripts, process HTML, or interpret any executable content.

---

## Revision History

| Version | Date | Changes |
|---|---|---|
| 1.0 | February 2026 | Initial design specification |
