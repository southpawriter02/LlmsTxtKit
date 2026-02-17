# Feature Roadmap: v0.0.1 → v1.0.0

**Author:** Ryan
**Version:** 1.0
**Last Updated:** February 2026
**Status:** Draft
**Prerequisites:** Read the [PRS](prs.md) and [Design Spec](design-spec.md) first — this document maps their requirements onto a phased delivery schedule with explicit implementation boundaries per version.

---

## How to Read This Document

Each version section below describes exactly what ships in that release: the types introduced, the public API surface added, the test coverage included, and the documentation updated. Versions are ordered by dependency — each one builds on the previous, and none skip ahead to functionality that depends on types not yet implemented.

The roadmap is structured around two guiding principles from the existing specs:

1. **Documentation-first** (from CONTRIBUTING.md § Documentation-First Workflow): spec documents, user stories, and test plans are written or updated *before* implementation code for each version. If a version introduces new public API surface, the XML doc comments and test cases are part of that version's deliverables, not a follow-up task.

2. **Core before Mcp** (from Design Spec § 1): `LlmsTxtKit.Core` is fully independent and has no knowledge of MCP. The roadmap implements all Core components before any MCP server work begins. This ensures the library is usable standalone from the earliest possible version.

### Version Numbering Convention

This project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). During the `0.x.y` pre-release phase, the API is not considered stable and may change between minor versions. The `1.0.0` release marks API stability — after that point, breaking changes require a major version bump.

- **0.0.x** — Foundation and scaffolding. No functional library code.
- **0.1.x through 0.5.x** — Incremental Core component implementation. Each minor version adds one major component.
- **0.6.x through 0.8.x** — MCP server implementation, building on the completed Core.
- **0.9.x** — Integration testing, polish, and release candidate preparation.
- **1.0.0** — Stable public release. All PRS success criteria (SC-1 through SC-11) met.

---

## Current State Assessment

Before describing where we're going, here's an honest accounting of where we are. This assessment was performed by reviewing every file in the repository as of February 2026.

### What Exists

| Asset | Status | Notes |
|---|---|---|
| Repository scaffolding | **Complete** | Solution file, four `.csproj` files, directory structure for all components, `.gitignore`, LICENSE (MIT). |
| README.md | **Complete** | Thorough project overview, API usage examples, MCP tool reference, repository structure map. |
| CONTRIBUTING.md | **Complete** | Dev setup, coding standards, naming conventions, testing conventions, PR guidelines. |
| CHANGELOG.md | **Skeleton** | `[Unreleased]` section with scaffolding note only. |
| PRS (specs/prs.md) | **Complete** | Problem statement, target users, 11 success criteria, 7 non-goals, assumptions, and constraints. |
| Design Spec (specs/design-spec.md) | **Complete** | Architecture diagram, all 5 Core component designs with key types and API signatures, all 5 MCP tool definitions, data flow trace, error handling strategy, configuration model, dependency choices, security considerations. |
| User Stories (specs/user-stories.md) | **Placeholder** | Contains TODO comments with category outlines but no actual user stories written. |
| Test Plan (specs/test-plan.md) | **Placeholder** | Contains TODO comments with section outlines but no actual test plan content. |
| `src/LlmsTxtKit.Core/` | **Empty** | All five component directories (`Parsing/`, `Fetching/`, `Validation/`, `Caching/`, `Context/`) exist but contain only `.gitkeep` files. Zero C# source files. |
| `src/LlmsTxtKit.Mcp/` | **Stub** | `Program.cs` exists with a placeholder `Main()` method (prints "not yet implemented"). `Tools/` and `Server/` directories contain only `.gitkeep` files. |
| `tests/` | **Empty** | Test project `.csproj` files exist. `TestData/valid/`, `TestData/malformed/`, `TestData/edge-cases/` directories exist but contain only `.gitkeep` files. No test source files. |
| `docs/` | **Empty** | `api/` and `images/` directories exist but contain only `.gitkeep` files. |

### What This Means

The project has excellent *design documentation* — the PRS and Design Spec are detailed enough to implement against directly. But it has **zero functional code**. Every source directory is empty. The two incomplete spec documents (User Stories and Test Plan) are prerequisites for the documentation-first workflow, so they need to be completed before their corresponding implementation phases begin.

The `.csproj` files are already configured with the right target framework (`net8.0`), nullable reference types enabled, XML documentation generation enabled, and NuGet metadata populated. The solution file wires everything together correctly. So the build infrastructure is ready — it just has nothing to build yet.

---

## v0.0.1 — Repository Scaffolding ✅ (Already Released)

**Theme:** Project initialization and design documentation.

This version represents the current state of the repository. It's already done.

### Deliverables

- Repository structure with solution file and four `.csproj` projects
- Complete PRS (`specs/prs.md`)
- Complete Design Spec (`specs/design-spec.md`)
- Complete README with API examples, MCP tool reference, and repository structure
- Complete CONTRIBUTING guidelines
- CHANGELOG skeleton
- MIT LICENSE
- `.gitignore` configuration
- Empty project directories with `.gitkeep` placeholders
- Empty test data directories with `.gitkeep` placeholders

### What This Version Does NOT Include

- Any functional C# code
- Any test files
- Completed User Stories or Test Plan documents

---

## v0.1.0 — Parsing Component

**Theme:** The foundation data model and parser — the types that every other component depends on.

**Why this is first:** Every other component in the system consumes `LlmsDocument`, `LlmsSection`, and `LlmsEntry`. The fetcher produces them. The validator inspects them. The cache stores them. The context generator expands them. Nothing else can be built until the parser and its output types exist.

### Spec Documents (Complete Before Code)

- **User Stories:** Write the Library Consumer stories for parsing (from the TODO outline in `specs/user-stories.md`):
  - "As a .NET developer, I want to parse an llms.txt file from a string so that I can work with its structure programmatically."
  - "As a .NET developer, I want to receive structured diagnostics for malformed input so that I can report problems to content authors."
  - "As a .NET developer, I want the parser to handle real-world llms.txt files (not just spec-perfect ones) so that I can work with production content reliably."
- **Test Plan:** Write the Parser section of the test plan (from the TODO outline in `specs/test-plan.md`), covering spec-compliant inputs, edge cases, and malformed inputs.

### Types Introduced

All types live in the `LlmsTxtKit.Core.Parsing` namespace. These are the data model types described in Design Spec § 2.1.

| Type | Kind | Purpose |
|---|---|---|
| `LlmsDocument` | Immutable record/class | The top-level parsed representation of an llms.txt file. Contains `Title`, `Summary`, `FreeformContent`, `Sections`, `RawContent`, and `Diagnostics`. |
| `LlmsSection` | Immutable record/class | An H2-delimited section within the document. Contains `Name`, `IsOptional`, and `Entries`. |
| `LlmsEntry` | Immutable record/class | A single link entry within a section. Contains `Url` (as `Uri`), `Title`, and `Description`. |
| `ParseDiagnostic` | Immutable record/class | A single diagnostic message produced during parsing. Contains `Severity` (enum: `Warning`, `Error`), `Message` (human-readable), and `Line` (line number in source, nullable). |
| `ParseSeverity` | Enum | `Warning`, `Error`. |
| `LlmsDocumentParser` | Static class | The parser itself. Single public method: `Parse(string content) → LlmsDocument`. |

### Behavioral Requirements (from PRS SC-1 and Design Spec § 2.1)

The parser must handle the following, aligned with the reference Python implementation (`miniparse.py`):

- Extract the required H1 title (first `# ` line)
- Extract the optional blockquote summary (first `> ` line after the H1)
- Extract freeform content between the summary and the first H2 (the "info" field)
- Split sections on `## ` headings only — H3+ headings are treated as content within the parent section
- Identify the `## Optional` section by case-sensitive exact match
- Parse entries using the canonical link pattern: `- [Title](URL): Description`
- Produce `ParseDiagnostic` entries (not exceptions) for malformed input
- Preserve `RawContent` for debugging

### Test Data

Populate `tests/TestData/valid/` with at least 5 real-world llms.txt files sourced from production websites (e.g., Anthropic, Cloudflare, Stripe, FastHTML, Vercel). Each file should include a comment header noting its source URL and capture date.

Populate `tests/TestData/malformed/` with at least 5 intentionally broken files covering: missing H1, malformed blockquote, plain text in sections instead of link lists, relative URLs, mixed link styles.

Populate `tests/TestData/edge-cases/` with at least 3 files covering: Unicode in titles/descriptions, empty file (just a title), file with only an Optional section.

### Test Coverage

- `ParserTests.cs` in `LlmsTxtKit.Core.Tests`:
  - `Parse_ValidMinimalFile_ExtractsTitle` — A file with only an H1 title and nothing else.
  - `Parse_ValidFullFile_ExtractsAllComponents` — A file with title, summary, freeform content, multiple sections, and entries.
  - `Parse_OptionalSection_MarkedCorrectly` — The `## Optional` section has `IsOptional == true`; other sections do not.
  - `Parse_EntryWithDescription_ExtractsAllParts` — `- [Title](URL): Description` produces correct `Title`, `Url`, and `Description`.
  - `Parse_EntryWithoutDescription_DescriptionIsNull` — `- [Title](URL)` produces `Description == null`.
  - `Parse_MissingH1_ProducesDiagnostic` — No H1 present → diagnostics include an `Error` severity entry with rule indicating missing title.
  - `Parse_MalformedBlockquote_ProducesDiagnostic` — Blockquote without proper format → diagnostics include a `Warning`.
  - `Parse_RealWorldFile_Anthropic_ParsesCorrectly` — Loads `tests/TestData/valid/anthropic.txt` and verifies section count, entry count, and title.
  - `Parse_RealWorldFile_Cloudflare_ParsesCorrectly` — Same pattern for Cloudflare's file.
  - `Parse_UnicodeContent_HandledCorrectly` — Unicode in titles and descriptions round-trips through the parser.
  - `Parse_VeryLargeFile_CompletesWithinTimeout` — A synthetic 1000+ entry file parses within a reasonable time bound (e.g., 5 seconds).
  - `Parse_EmptyString_ProducesDiagnostic` — Empty input produces an error diagnostic, not an exception.
  - `Parse_NullInput_ThrowsArgumentException` — Null input is a programming error and throws.
  - `Parse_H3Headings_TreatedAsContentNotSections` — H3 headings within an H2 section remain part of that section's content.
  - `Parse_RelativeUrl_ProducesDiagnostic` — Entries with relative URLs produce a warning diagnostic.

### CHANGELOG Entry

```
## [0.1.0] - YYYY-MM-DD

### Added
- `LlmsDocument`, `LlmsSection`, `LlmsEntry` immutable data model types.
- `LlmsDocumentParser.Parse()` static method for parsing llms.txt content.
- `ParseDiagnostic` and `ParseSeverity` for structured error reporting.
- Test data corpus: real-world valid files, malformed files, and edge cases.
- Parser unit tests covering spec-compliant, malformed, and edge-case inputs.
- User stories for library consumer parsing scenarios.
- Test plan section for parser coverage.
```

---

## v0.2.0 — Fetching Component

**Theme:** Infrastructure-aware HTTP retrieval — turning a domain name into a `FetchResult` with structured status information.

**Why this is second:** The fetcher is the primary entry point for most real-world usage. It produces `LlmsDocument` (via the parser from v0.1.0) and `FetchResult` objects that downstream components consume. The validator needs fetch metadata. The cache stores fetch results. The context generator uses the fetcher to retrieve linked content.

### Spec Documents (Update Before Code)

- **User Stories:** Add Library Consumer stories for fetching:
  - "As a .NET developer, I want to fetch an llms.txt file by domain name so that I don't have to construct URLs manually."
  - "As a .NET developer, I want structured status information on fetch failures so that I can distinguish between 'file doesn't exist' and 'blocked by WAF'."
  - "As a .NET developer, I want configurable timeouts and retries so that I can tune behavior for my deployment environment."
  - "As a .NET developer, I want to inject my own `HttpClient` so that I can use `IHttpClientFactory` for connection lifecycle management."
- **Test Plan:** Add the Fetcher and Integration Test sections.

### Types Introduced

All types live in `LlmsTxtKit.Core.Fetching`.

| Type | Kind | Purpose |
|---|---|---|
| `FetchStatus` | Enum | `Success`, `NotFound`, `Blocked`, `RateLimited`, `DnsFailure`, `Timeout`, `Error`. Seven distinct outcome categories per Design Spec § 2.2. |
| `FetchResult` | Immutable class | The structured outcome of a fetch attempt. Contains `Status`, `Document` (nullable — populated only on `Success`), `RawContent`, `StatusCode`, `ResponseHeaders`, `BlockReason`, `RetryAfter`, `ErrorMessage`, and `Duration`. |
| `FetcherOptions` | Options class | Configuration for the fetcher: `UserAgent`, `TimeoutSeconds`, `MaxRetries`, `RetryDelayMs`, `AcceptHeaderOverride`. |
| `LlmsTxtFetcher` | Service class | The fetcher itself. Constructor accepts optional `FetcherOptions` and optional `HttpClient`. Single async method: `FetchAsync(string domain, CancellationToken) → Task<FetchResult>`. |

### Behavioral Requirements (from PRS SC-2 and Design Spec § 2.2)

- Construct URL as `https://{domain}/llms.txt` from the input domain string
- Return structured `FetchResult` — never throw on non-success HTTP responses
- Classify responses into the seven `FetchStatus` categories
- Detect Cloudflare WAF blocks via response headers (`cf-ray`, `cf-mitigated`, `server: cloudflare`) and response body patterns (challenge pages)
- Populate `BlockReason` with a human-readable diagnosis when WAF blocking is detected
- Extract `Retry-After` header on 429 responses and populate `RetryAfter` as `TimeSpan`
- Support configurable retry logic with exponential backoff for transient failures
- On `Success`, parse the response body using `LlmsDocumentParser.Parse()` and populate `FetchResult.Document`
- Accept `CancellationToken` for async cancellation
- Record `Duration` for performance monitoring
- Throw `ArgumentException` on null/empty domain input (programming error)
- Throw `ObjectDisposedException` if the underlying `HttpClient` is disposed during a request (unrecoverable)

### Test Coverage

- `FetcherTests.cs` using a mock `HttpMessageHandler` (no live HTTP):
  - `FetchAsync_Success200_ReturnsParsedDocument` — Mock returns 200 with valid llms.txt content.
  - `FetchAsync_NotFound404_ReturnsNotFoundStatus` — Mock returns 404.
  - `FetchAsync_Blocked403WithCloudflareHeaders_ReturnsBlockedStatus` — Mock returns 403 with `cf-ray` and `server: cloudflare` headers.
  - `FetchAsync_Blocked403Generic_ReturnsBlockedWithoutDiagnosis` — Mock returns 403 without identifiable WAF headers.
  - `FetchAsync_RateLimited429_ReturnsRateLimitedWithRetryAfter` — Mock returns 429 with `Retry-After` header.
  - `FetchAsync_DnsFailure_ReturnsDnsFailureStatus` — Mock throws `HttpRequestException` simulating DNS resolution failure.
  - `FetchAsync_Timeout_ReturnsTimeoutStatus` — Mock delays beyond `TimeoutSeconds`.
  - `FetchAsync_ServerError500_ReturnsErrorStatus` — Mock returns 500.
  - `FetchAsync_RetryOnTransientFailure_RetriesConfiguredTimes` — Mock fails N times then succeeds; verify retry count matches `MaxRetries`.
  - `FetchAsync_CancellationRequested_ThrowsOperationCanceledException` — Verify cancellation token is respected.
  - `FetchAsync_NullDomain_ThrowsArgumentException` — Null input throws.
  - `FetchAsync_CustomUserAgent_SentInRequestHeaders` — Verify the configured `UserAgent` appears in the outgoing request.
  - `FetchAsync_Duration_PopulatedOnAllOutcomes` — Every `FetchResult` has a non-zero `Duration`.

### CHANGELOG Entry

```
## [0.2.0] - YYYY-MM-DD

### Added
- `FetchStatus` enum with seven outcome categories.
- `FetchResult` structured result type with status, document, headers, and diagnostics.
- `FetcherOptions` for configuring timeouts, retries, and user-agent.
- `LlmsTxtFetcher` with `FetchAsync()` method and WAF block detection heuristics.
- Fetcher unit tests using mock HTTP handlers.
- User stories for library consumer fetching scenarios.
- Test plan sections for fetcher and integration test scenarios.
```

---

## v0.3.0 — Validation Component

**Theme:** Spec-compliance checking and content health assessment.

**Why this is third:** The validator inspects `LlmsDocument` objects (from v0.1.0) and optionally uses the fetcher (from v0.2.0) for URL reachability and freshness checks. It's a consumer of both prior components.

### Spec Documents (Update Before Code)

- **User Stories:** Add Library Consumer stories for validation:
  - "As a .NET developer, I want to validate an llms.txt document against the spec so that I can flag structural problems before using the content."
  - "As a .NET developer, I want to check whether linked URLs are reachable so that I can identify broken references."
  - "As a .NET developer, I want to detect stale content via Last-Modified comparison so that I can warn users about potentially outdated information."
  - "As a documentation maintainer, I want to run validation in CI/CD so that spec violations are caught before deployment."

### Types Introduced

All types live in `LlmsTxtKit.Core.Validation`.

| Type | Kind | Purpose |
|---|---|---|
| `ValidationSeverity` | Enum | `Error`, `Warning`. |
| `ValidationIssue` | Immutable record/class | A single finding: `Severity`, `Rule` (machine-readable ID string), `Message` (human-readable), `Location` (nullable — line number or section name). |
| `ValidationReport` | Immutable class | Aggregation of findings: `IsValid` (true if zero errors), `Errors`, `Warnings`, `AllIssues` (combined, sorted). |
| `ValidationOptions` | Options class | `CheckLinkedUrls` (default: `false`), `CheckFreshness` (default: `false`), `UrlCheckTimeout` (default: 10 seconds). |
| `IValidationRule` | Interface | Single method: `Task<IEnumerable<ValidationIssue>> EvaluateAsync(LlmsDocument, ValidationOptions)`. Each rule is independent and individually testable. |
| `LlmsDocumentValidator` | Service class | Orchestrates all `IValidationRule` implementations. Single async method: `ValidateAsync(LlmsDocument, ValidationOptions?) → Task<ValidationReport>`. |

### Validation Rules Implemented (from Design Spec § 2.3)

| Rule ID | Severity | Condition |
|---|---|---|
| `REQUIRED_H1_MISSING` | Error | Document has no `Title` (empty or null). |
| `MULTIPLE_H1_FOUND` | Error | Parser detected multiple H1 headings (captured via `ParseDiagnostic`). |
| `BLOCKQUOTE_MALFORMED` | Warning | Summary exists but blockquote formatting doesn't match expected pattern. |
| `SECTION_EMPTY` | Warning | An H2 section contains zero entries. |
| `ENTRY_URL_INVALID` | Error | An entry's URL does not parse as a valid absolute HTTP/HTTPS URI. |
| `ENTRY_URL_UNREACHABLE` | Warning | HTTP HEAD to a linked URL returned non-200 (only when `CheckLinkedUrls == true`). |
| `ENTRY_URL_REDIRECTED` | Warning | HTTP HEAD to a linked URL returned 3xx (only when `CheckLinkedUrls == true`). |
| `CONTENT_STALE` | Warning | llms.txt `Last-Modified` is older than a linked page's `Last-Modified` (only when `CheckFreshness == true`). |
| `UNEXPECTED_HEADING_LEVEL` | Warning | Headings other than H1/H2 found at the structural level. |
| `CONTENT_OUTSIDE_STRUCTURE` | Warning | Content exists that doesn't belong to any defined structural element (orphaned text). |

### Test Coverage

- `ValidatorTests.cs` with per-rule test methods:
  - One test per rule verifying it fires on the expected condition
  - One test per rule verifying it does NOT fire on valid input
  - `ValidateAsync_FullyValidDocument_IsValidTrue` — A well-formed document produces zero errors.
  - `ValidateAsync_MultipleIssues_AllReported` — A document with several problems reports all of them, not just the first.
  - `ValidateAsync_CheckLinkedUrlsFalse_SkipsUrlChecks` — URL reachability rules are not evaluated when the option is off.
  - `ValidateAsync_CheckFreshnessFalse_SkipsFreshnessChecks` — Staleness rule is not evaluated when the option is off.
  - `ValidateAsync_WarningsOnly_IsValidStillTrue` — `IsValid` reflects only errors, not warnings.

### CHANGELOG Entry

```
## [0.3.0] - YYYY-MM-DD

### Added
- `ValidationReport`, `ValidationIssue`, `ValidationSeverity` types.
- `ValidationOptions` for configuring URL checks and freshness checks.
- `IValidationRule` interface for extensible, independently-testable rules.
- `LlmsDocumentValidator` service with 10 built-in validation rules.
- Per-rule unit tests for all validation rules.
- User stories for library consumer validation scenarios.
- Test plan section for validator coverage.
```

---

## v0.4.0 — Caching Component

**Theme:** Avoid redundant fetches and provide stale-while-revalidate semantics for inference-time usage.

**Why this is fourth:** The cache stores `LlmsDocument` and `FetchResult` objects (from v0.1.0 and v0.2.0) and optionally associates `ValidationReport` (from v0.3.0) entries. It's consumed by the context generator (v0.5.0) and the MCP tools (v0.6.0+), both of which need cached data to avoid hammering origin servers.

### Spec Documents (Update Before Code)

- **User Stories:** Add Library Consumer stories for caching:
  - "As a .NET developer, I want parsed documents cached with configurable TTL so that repeated lookups don't trigger redundant HTTP requests."
  - "As a .NET developer, I want stale-while-revalidate behavior so that my AI inference pipeline gets immediate responses even when cache entries are expired."
  - "As a .NET developer, I want optional file-backed persistence so that my cache survives process restarts."

### Types Introduced

All types live in `LlmsTxtKit.Core.Caching`.

| Type | Kind | Purpose |
|---|---|---|
| `CacheEntry` | Immutable class | A cached item: `Document`, `ValidationReport` (nullable), `FetchedAt`, `ExpiresAt`, `HttpHeaders`, `FetchResult`. |
| `CacheOptions` | Options class | `DefaultTtl` (default: 1 hour), `MaxEntries` (default: 1000), `StaleWhileRevalidate` (default: `true`), `BackingStore` (nullable). |
| `ICacheBackingStore` | Interface | `SaveAsync(string key, CacheEntry)` and `LoadAsync(string key) → Task<CacheEntry?>`. Abstraction for persistence beyond in-memory. |
| `FileCacheBackingStore` | Class | Implementation of `ICacheBackingStore` that serializes cache entries to JSON files in a configurable directory. |
| `LlmsTxtCache` | Service class | Domain-keyed cache. Methods: `GetAsync(string domain)`, `SetAsync(string domain, CacheEntry)`, `InvalidateAsync(string domain)`, `ClearAsync()`. Internally uses `ConcurrentDictionary` for thread safety. |

### Behavioral Requirements (from PRS SC-4 and Design Spec § 2.4)

- Domain-keyed: `Get("anthropic.com")`, not full URLs (each domain has exactly one `/llms.txt` path)
- TTL-based expiration with configurable default
- LRU eviction when `MaxEntries` is exceeded
- Stale-while-revalidate: when enabled, returns expired entries immediately while triggering an async background refresh
- Thread-safe via `ConcurrentDictionary` for the in-memory store
- Optional `ICacheBackingStore` for persistence; `FileCacheBackingStore` serializes entries using `System.Text.Json`

### Test Coverage

- `CacheTests.cs`:
  - `GetAsync_CachedEntry_ReturnsEntry` — Store then retrieve.
  - `GetAsync_ExpiredEntry_StaleWhileRevalidateEnabled_ReturnsStaleEntry` — Expired entry is returned when the option is on.
  - `GetAsync_ExpiredEntry_StaleWhileRevalidateDisabled_ReturnsNull` — Expired entry is not returned when the option is off.
  - `GetAsync_NoCachedEntry_ReturnsNull` — Miss returns null.
  - `SetAsync_ExceedsMaxEntries_EvictsOldest` — When full, the oldest entry is evicted.
  - `InvalidateAsync_RemovesEntry` — Specific domain entry is removed.
  - `ClearAsync_RemovesAllEntries` — Cache is emptied.
  - `FileCacheBackingStore_SaveAndLoad_RoundTrips` — Serialize to file, deserialize, compare.
  - `ConcurrentAccess_ThreadSafe` — Multiple threads reading/writing simultaneously don't corrupt state.

### CHANGELOG Entry

```
## [0.4.0] - YYYY-MM-DD

### Added
- `CacheEntry`, `CacheOptions` types for configurable document caching.
- `ICacheBackingStore` interface for pluggable persistence.
- `FileCacheBackingStore` for JSON file-backed cache persistence.
- `LlmsTxtCache` service with domain-keyed storage, TTL, LRU eviction, and stale-while-revalidate.
- Cache unit tests covering TTL, eviction, persistence, and concurrent access.
- User stories for library consumer caching scenarios.
- Test plan section for cache coverage.
```

---

## v0.5.0 — Context Generation Component

**Theme:** Expand an `LlmsDocument` into an LLM-ready context string by fetching linked content and applying token budgeting.

**Why this is fifth:** The context generator is the highest-level component in Core. It depends on the parser (v0.1.0) for document structure, the fetcher (v0.2.0) for retrieving linked Markdown content, and ideally the cache (v0.4.0) to avoid redundant fetches of linked URLs. It's the last Core component needed before the MCP server can be built.

### Spec Documents (Update Before Code)

- **User Stories:** Add Library Consumer stories for context generation:
  - "As a .NET developer, I want to generate an LLM-ready context string from an llms.txt document so that I can inject curated documentation into AI prompts."
  - "As a .NET developer, I want to set a token budget so that the generated context fits within my model's context window."
  - "As a .NET developer, I want Optional sections excluded by default (matching the reference implementation) so that I get the most important content first."
  - "As a .NET developer, I want sections wrapped in XML tags so that the LLM can attribute information to its source."

### Types Introduced

All types live in `LlmsTxtKit.Core.Context`.

| Type | Kind | Purpose |
|---|---|---|
| `ContextOptions` | Options class | `MaxTokens` (nullable — no limit if null), `IncludeOptional` (default: `false`, matching reference implementation), `WrapSectionsInXml` (default: `true`), `TokenEstimator` (nullable — defaults to words÷4 heuristic). |
| `ContextResult` | Immutable class | The output: `Content` (the generated context string), `ApproximateTokenCount`, `SectionsIncluded` (list of section names), `SectionsOmitted` (list of section names dropped due to budget), `SectionsTruncated` (list of section names that were partially included), `FetchErrors` (list of URLs that failed to fetch with error details). |
| `ContextGenerator` | Service class | Constructor accepts optional `LlmsTxtFetcher` and optional `LlmsTxtCache`. Single async method: `GenerateAsync(LlmsDocument, ContextOptions?) → Task<ContextResult>`. |

### Behavioral Requirements (from PRS SC-5 and Design Spec § 2.5)

- Iterate over `LlmsDocument.Sections`, respecting `IncludeOptional`
- For each entry in each section, fetch the linked URL's content using the fetcher
- Use the cache (if provided) to avoid redundant fetches of linked content
- Wrap sections in `<section name="SectionName">...</section>` XML tags when `WrapSectionsInXml` is true (the generic wrapper approach per Design Spec § 2.5, not the reference implementation's fastcore FT approach)
- Apply token budgeting: drop Optional sections first, then truncate remaining sections at sentence boundaries
- Append truncation indicator (`[... content truncated to fit token budget ...]`) when content is truncated
- Include error placeholders for URLs that failed to fetch (not silently omitted)
- Strip HTML comments and base64-embedded images from fetched Markdown content (per reference implementation behavior)
- Default `TokenEstimator` uses words÷4 heuristic; callers can supply their own function

### Test Coverage

- `ContextTests.cs`:
  - `GenerateAsync_SingleSection_ReturnsContent` — One section with one entry produces content.
  - `GenerateAsync_MultipleSections_AllIncluded` — All non-Optional sections are included.
  - `GenerateAsync_OptionalSectionExcludedByDefault` — `IncludeOptional == false` skips the Optional section.
  - `GenerateAsync_OptionalSectionIncludedWhenRequested` — `IncludeOptional == true` includes it.
  - `GenerateAsync_XmlWrapping_SectionsWrappedInTags` — Output contains `<section name="...">` tags.
  - `GenerateAsync_XmlWrappingDisabled_NoTags` — Output does not contain XML tags.
  - `GenerateAsync_TokenBudgetExceeded_OptionalDroppedFirst` — When over budget with Optional included, Optional is dropped before other sections.
  - `GenerateAsync_TokenBudgetExceeded_TruncatesAtSentenceBoundary` — Truncated content ends at a sentence boundary, not mid-word.
  - `GenerateAsync_TruncationIndicator_Appended` — Truncated output includes the truncation marker string.
  - `GenerateAsync_FetchFailure_IncludesErrorPlaceholder` — An entry whose URL fails to fetch appears as a placeholder, not a silent omission.
  - `GenerateAsync_CustomTokenEstimator_Used` — A custom estimator function is called and its return values drive budget decisions.
  - `GenerateAsync_HtmlComments_StrippedFromContent` — HTML comments in fetched Markdown are removed.

### CHANGELOG Entry

```
## [0.5.0] - YYYY-MM-DD

### Added
- `ContextOptions`, `ContextResult` types for configurable context generation.
- `ContextGenerator` service with token budgeting, XML wrapping, and Optional section handling.
- Context generation unit tests covering budgeting, truncation, and error handling.
- User stories for library consumer context generation scenarios.
- Test plan section for context generator coverage.

### Changed
- LlmsTxtKit.Core library is now feature-complete for v1.0 scope.
```

---

## v0.6.0 — MCP Server Foundation and `llmstxt_discover` Tool

**Theme:** Stand up the MCP server infrastructure and ship the first tool that AI agents can call.

**Why this is sixth:** All five Core components are complete. Now the MCP server can be built as a thin protocol adapter over Core. The `llmstxt_discover` tool is the natural first tool — it's the entry point for any agent interaction (check if a site has an llms.txt file before doing anything else).

### Spec Documents (Update Before Code)

- **User Stories:** Write the MCP Agent Consumer stories (from the TODO outline in `specs/user-stories.md`):
  - "As an AI agent, I want to discover whether a site has an llms.txt file so that I can decide whether to use llms.txt-curated content or fall back to standard web retrieval."
- **Test Plan:** Add the MCP Protocol Tests section.

### Types Introduced

Types span `LlmsTxtKit.Mcp.Server` and `LlmsTxtKit.Mcp.Tools`.

| Type | Kind | Purpose |
|---|---|---|
| MCP Server host | Infrastructure | DI container setup, MCP transport configuration (stdio), tool registration, shutdown handling. Replaces the current stub in `Program.cs`. |
| `DiscoverTool` | MCP Tool class | Implements the `llmstxt_discover` tool definition from Design Spec § 3.1. Delegates to `LlmsTxtFetcher` and `LlmsTxtCache`. |

### Behavioral Requirements

- `Program.cs` wires up the DI container with Core services (`LlmsTxtFetcher`, `LlmsTxtCache`, `LlmsDocumentValidator`, `ContextGenerator`)
- MCP transport uses stdio (standard input/output) for the initial implementation — this is the most common transport for local MCP servers and the one Claude Desktop expects
- The `llmstxt_discover` tool accepts a `domain` parameter, calls `FetchAsync`, and returns a JSON response containing: `found` (boolean), `status` (string), and if found: `title`, `summary`, `sections` (array of `{ name, entry_count }`), and `diagnostics`

### Test Coverage

- `ToolResponseTests.cs` in `LlmsTxtKit.Mcp.Tests`:
  - `DiscoverTool_ValidDomain_ReturnsStructuredResponse` — Mock a successful fetch and verify the JSON response shape.
  - `DiscoverTool_NotFoundDomain_ReturnsNotFoundStatus` — Mock a 404 and verify the response indicates `found: false`.
  - `DiscoverTool_BlockedDomain_ReturnsBlockedStatusWithReason` — Mock a WAF block and verify `block_reason` is populated.
  - `DiscoverTool_MissingDomainParameter_ReturnsError` — Missing required parameter produces a valid MCP error response.

### CHANGELOG Entry

```
## [0.6.0] - YYYY-MM-DD

### Added
- MCP server infrastructure: DI setup, stdio transport, tool registration.
- `llmstxt_discover` MCP tool for checking whether a domain has an llms.txt file.
- MCP protocol compliance tests for the discover tool.
- User stories for MCP agent discovery scenarios.
- Test plan section for MCP protocol tests.
```

---

## v0.7.0 — `llmstxt_fetch_section`, `llmstxt_validate`, and `llmstxt_context` Tools

**Theme:** Ship the three "workhorse" MCP tools that cover the most common agent workflows.

**Why these are grouped:** These three tools are the core of the agent experience. They're grouped into a single release because together they cover the complete discover → fetch → validate → generate-context workflow. Each is a thin wrapper over an already-tested Core component, so the implementation risk per tool is low.

### Spec Documents (Update Before Code)

- **User Stories:** Add remaining MCP Agent Consumer stories:
  - "As an AI agent, I want to fetch a specific section's content so that I can retrieve only the documentation relevant to the user's question."
  - "As an AI agent, I want to validate a site's llms.txt compliance so that I can assess confidence in the content before using it."
  - "As an AI agent, I want to generate full LLM-ready context so that I can inject curated documentation into my reasoning."

### Tools Implemented

| Tool | Design Spec Reference | Core Dependency |
|---|---|---|
| `llmstxt_fetch_section` | § 3.2 | `LlmsTxtFetcher`, `LlmsTxtCache`, `ContextGenerator` (for section content expansion) |
| `llmstxt_validate` | § 3.3 | `LlmsDocumentValidator`, `LlmsTxtFetcher`, `LlmsTxtCache` |
| `llmstxt_context` | § 3.4 | `ContextGenerator`, `LlmsTxtFetcher`, `LlmsTxtCache` |

### Test Coverage

- Additional tests in `ToolResponseTests.cs`:
  - `FetchSectionTool_ValidSection_ReturnsContent` — Verify content is returned for a named section.
  - `FetchSectionTool_NonexistentSection_ReturnsError` — Requesting a section that doesn't exist produces a clear error.
  - `ValidateTool_ValidDocument_ReturnsIsValidTrue` — A well-formed document validates successfully.
  - `ValidateTool_InvalidDocument_ReturnsIssues` — A malformed document returns structured issues.
  - `ContextTool_ValidDomain_ReturnsContextString` — Verify the response includes `content`, `token_count`, and `sections_included`.
  - `ContextTool_WithTokenBudget_RespectsLimit` — The returned token count is at or below the specified budget.

### CHANGELOG Entry

```
## [0.7.0] - YYYY-MM-DD

### Added
- `llmstxt_fetch_section` MCP tool for retrieving specific section content.
- `llmstxt_validate` MCP tool for running spec-compliance and health checks.
- `llmstxt_context` MCP tool for generating full LLM-ready context strings.
- MCP protocol compliance tests for all three tools.
- User stories for MCP agent fetch, validate, and context generation scenarios.
```

---

## v0.8.0 — `llmstxt_compare` Tool

**Theme:** Ship the final MCP tool, completing the full five-tool suite defined in the Design Spec.

**Why this is separate:** The `llmstxt_compare` tool is architecturally distinct from the other four. It doesn't just work with llms.txt files — it fetches *both* the HTML page and the Markdown version, performs comparative analysis, and produces metrics (size ratios, token ratios, freshness deltas). It was specifically designed to support the Context Collapse Mitigation Benchmark study (see PRS § 6 and Design Spec § 3.5), and its implementation involves HTML-to-text conversion logic that the other tools don't need.

### Spec Documents (Update Before Code)

- **User Stories:** Add the comparison-focused MCP Agent Consumer story:
  - "As an AI agent, I want to compare HTML and Markdown versions of the same page so that I can measure the benefit of llms.txt-curated content over standard web retrieval."

### Tool Implemented

| Tool | Design Spec Reference | Core Dependency |
|---|---|---|
| `llmstxt_compare` | § 3.5 | `LlmsTxtFetcher` (for both HTML and Markdown fetches), `LlmsTxtCache` |

### Additional Implementation Details

- HTML-to-text stripping: strip HTML tags from the fetched HTML page to simulate how retrieval pipelines process HTML before passing it to an LLM. This is a utility method, not a full HTML parser — it strips tags and normalizes whitespace.
- Markdown version lookup: check for `{url}.md` following the llms.txt convention.
- Metrics: `html_size`, `markdown_size`, `size_ratio`, `html_token_estimate`, `markdown_token_estimate`, `token_ratio`, `freshness_delta`, `markdown_available`.

### Test Coverage

- Additional tests in `ToolResponseTests.cs`:
  - `CompareTool_MarkdownVersionAvailable_ReturnsBothVersions` — Both HTML and Markdown are fetched and compared.
  - `CompareTool_NoMarkdownVersion_ReturnsHtmlOnlyWithFlag` — `markdown_available: false` when no `.md` version exists.
  - `CompareTool_SizeAndTokenRatios_CalculatedCorrectly` — Verify ratio math is correct.
  - `CompareTool_FreshnessDelta_PopulatedWhenHeadersAvailable` — When both versions have `Last-Modified` headers, the delta is calculated.

### CHANGELOG Entry

```
## [0.8.0] - YYYY-MM-DD

### Added
- `llmstxt_compare` MCP tool for HTML vs. Markdown content comparison.
- HTML-to-text stripping utility for comparison metrics.
- MCP protocol compliance tests for the compare tool.
- User story for MCP agent comparison scenarios.

### Changed
- All five MCP tools are now operational (feature-complete for v1.0 scope).
```

---

## v0.9.0 — Integration Testing, Polish, and Release Candidate

**Theme:** End-to-end validation, documentation completeness, and pre-release hardening.

**Why this is the penultimate version:** All functional code is complete. This version is about confidence — making sure everything works together, the documentation is complete and accurate, and the packages are ready for public consumption.

### Integration Tests

These tests use a mock HTTP server (not live network calls) to simulate realistic multi-step workflows end-to-end. They live in a new `LlmsTxtKit.Integration.Tests` project (or a clearly-marked folder within the existing test projects).

- **End-to-end discovery workflow:** Call `llmstxt_discover` via the MCP server, then `llmstxt_fetch_section`, then `llmstxt_context` — verify the full agent workflow produces correct output.
- **WAF block graceful degradation:** Simulate a domain where the llms.txt fetch is blocked; verify the MCP tools return structured error information that an agent can reason about.
- **Cache integration:** Fetch a domain, then fetch it again within TTL — verify the second call hits the cache (no second HTTP request).
- **Stale-while-revalidate integration:** Fetch a domain, let the TTL expire, fetch again — verify the stale result is returned immediately.
- **Token budget end-to-end:** Generate context with a tight token budget — verify the output respects the budget and reports which sections were included/omitted.
- **Validation with URL checks:** Validate a document where some linked URLs are reachable and some are not — verify the report correctly identifies both.
- **Large file handling:** Feed a synthetic 1000+ entry llms.txt file through the full pipeline (parse → validate → cache → generate context) — verify it completes within reasonable time and memory bounds.

### Documentation Completeness Audit

- Verify every public type, method, property, and parameter in `LlmsTxtKit.Core` has XML documentation comments
- Verify the README's API examples compile and produce the described output (or update them if the API surface diverged during implementation)
- Verify `specs/user-stories.md` contains all stories written during v0.1.0 through v0.8.0
- Verify `specs/test-plan.md` contains all test plan sections written during v0.1.0 through v0.8.0
- Generate API reference documentation in `docs/api/` from the XML doc comments

### Package Validation

- Verify `dotnet pack` produces valid `.nupkg` files for both `LlmsTxtKit.Core` and `LlmsTxtKit.Mcp`
- Verify package metadata (ID, version, description, license, tags, repository URL) is correct
- Verify the `.nupkg` files install cleanly in a fresh project via `dotnet add package` (local NuGet source)
- Verify `LlmsTxtKit.Core` can be used without `LlmsTxtKit.Mcp` (no transitive dependency leakage)

### Polish Items

- Review and finalize `.gitignore` (ensure build artifacts, NuGet packages, IDE files are excluded)
- Add architecture diagrams to `docs/images/` if the Design Spec's ASCII diagrams benefit from visual versions
- Final pass on CONTRIBUTING.md to ensure it reflects the actual development experience (not just the pre-implementation plan)

### CHANGELOG Entry

```
## [0.9.0] - YYYY-MM-DD

### Added
- Integration test suite covering end-to-end workflows.
- Generated API reference documentation in `docs/api/`.

### Changed
- Completed user stories document (`specs/user-stories.md`).
- Completed test plan document (`specs/test-plan.md`).
- README API examples verified against actual implementation.
- Documentation completeness audit: all public API surface has XML doc comments.

### Fixed
- [Any bugs discovered during integration testing — TBD.]
```

---

## v1.0.0 — Stable Public Release

**Theme:** Version stamp, NuGet publication, and public announcement.

**Why this is the final version:** All PRS success criteria (SC-1 through SC-11) are met. All Design Spec components are implemented. All MCP tools are operational. The documentation is complete. The test suite passes. The packages build and install cleanly.

### PRS Success Criteria Checklist

| Criterion | Description | Satisfied By |
|---|---|---|
| SC-1 | Spec-compliant parsing | v0.1.0 |
| SC-2 | Resilient fetching | v0.2.0 |
| SC-3 | Spec-aware validation | v0.3.0 |
| SC-4 | Configurable caching | v0.4.0 |
| SC-5 | Context generation with token budgeting | v0.5.0 |
| SC-6 | Five MCP tools operational | v0.8.0 |
| SC-7 | XML documentation coverage | v0.9.0 (audit) |
| SC-8 | Test coverage | v0.9.0 (integration) |
| SC-9 | Real-world test corpus | v0.1.0 (initial), expanded through v0.9.0 |
| SC-10 | NuGet publishable | v0.9.0 (package validation) |
| SC-11 | Idiomatic .NET | Enforced throughout via coding standards |

### Release Actions

- Bump `<Version>` in both `.csproj` files from `0.9.0` to `1.0.0`
- Finalize `CHANGELOG.md`: move all `[Unreleased]` content into a `[1.0.0]` section with the release date
- Tag the release in Git: `git tag v1.0.0`
- Publish to NuGet: `dotnet nuget push` for both packages
- Update README badges (uncomment the NuGet badge lines)
- Cross-reference the release in the [Unified Roadmap](https://github.com/southpawriter02/llmstxt-research/blob/main/ROADMAP.md) in the research repository

### CHANGELOG Entry

```
## [1.0.0] - YYYY-MM-DD

### Changed
- Bumped version to 1.0.0 (stable public release).
- All PRS success criteria (SC-1 through SC-11) met.
- NuGet packages published: LlmsTxtKit.Core 1.0.0, LlmsTxtKit.Mcp 1.0.0.
```

---

## Dependency Graph Summary

This diagram shows which versions depend on which. It explains why the ordering is what it is — you can't build a later version without the earlier ones being complete.

```
v0.0.1  Repository Scaffolding
  │
  v
v0.1.0  Parsing ─────────────────────────────────────────────────┐
  │                                                               │
  v                                                               │
v0.2.0  Fetching (uses Parser from v0.1.0) ─────────────────┐    │
  │                                                          │    │
  v                                                          │    │
v0.3.0  Validation (uses Parser + Fetcher)                   │    │
  │                                                          │    │
  v                                                          │    │
v0.4.0  Caching (stores Parser + Fetcher outputs)            │    │
  │                                                          │    │
  v                                                          │    │
v0.5.0  Context Generation (uses Parser + Fetcher + Cache) ──┘    │
  │                                                               │
  v                                                               │
v0.6.0  MCP Server + Discover Tool (wraps all Core) ──────────────┘
  │
  v
v0.7.0  Fetch Section + Validate + Context Tools
  │
  v
v0.8.0  Compare Tool (feature-complete)
  │
  v
v0.9.0  Integration Testing + Polish
  │
  v
v1.0.0  Stable Release
```

---

## Estimated Scope Per Version

These are rough sizing estimates based on the Design Spec's type descriptions. They're meant to help with planning, not to be commitments.

| Version | New Types | New Test Methods (approx.) | Key Risk |
|---|---|---|---|
| v0.1.0 | 6 types | ~16 tests | Parser correctness against reference implementation |
| v0.2.0 | 4 types | ~13 tests | Mock HTTP handler fidelity for WAF simulation |
| v0.3.0 | 6 types + 10 rule classes | ~15 tests | Rule completeness and severity calibration |
| v0.4.0 | 5 types | ~9 tests | Thread safety in concurrent cache access |
| v0.5.0 | 3 types | ~12 tests | Token budgeting accuracy and truncation logic |
| v0.6.0 | 2 types + infrastructure | ~4 tests | MCP protocol compliance (transport layer) |
| v0.7.0 | 3 tool classes | ~6 tests | Tool response schema correctness |
| v0.8.0 | 1 tool class + utility | ~4 tests | HTML stripping edge cases |
| v0.9.0 | 0 types (integration only) | ~7+ integration tests | Discovering bugs in cross-component interaction |
| v1.0.0 | 0 types (release only) | 0 new tests | NuGet publication process |

---

## Risk Callouts

These are issues that could disrupt the roadmap. They're flagged here so they can be monitored rather than discovered as surprises.

**Risk 1: MCP SDK availability for .NET.** The Design Spec doesn't specify which MCP SDK to use for the server implementation. As of this writing, the official MCP SDK from Anthropic is available for TypeScript and Python. A community .NET MCP SDK may exist, or the protocol layer may need to be implemented directly against the MCP specification. This affects v0.6.0 scope. Mitigating action: research available .NET MCP libraries before starting v0.6.0 and adjust the implementation plan accordingly.

**Risk 2: Reference implementation behavioral edge cases.** PRS SC-1 requires behavioral alignment with the Python reference parser (`miniparse.py`). Some edge cases may not be documented in the spec and can only be discovered by running the reference implementation against test inputs. Mitigating action: during v0.1.0, create a validation harness that runs the same inputs through both the Python reference parser and LlmsTxtKit's parser and compares outputs.

**Risk 3: Real-world llms.txt file diversity.** The test data corpus (v0.1.0) depends on sourcing production llms.txt files. Some sites may change or remove their files, and the files may expose parser edge cases not anticipated in the Design Spec. Mitigating action: capture files with timestamps and source URLs. Treat unexpected parsing behavior as test cases, not blockers.

**Risk 4: Token estimation accuracy.** The words÷4 heuristic is a rough approximation. If users report that token budgets are consistently off by a large margin for specific models, the default estimator may need refinement. Mitigating action: document the heuristic's limitations clearly and ensure the `TokenEstimator` extensibility point works well so users can plug in model-specific tokenizers.

---

## Revision History

| Version | Date | Changes |
|---|---|---|
| 1.0 | February 2026 | Initial feature roadmap |
