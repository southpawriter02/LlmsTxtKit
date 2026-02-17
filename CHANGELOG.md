# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

## [0.9.1] - 2026-02-17

### Added

- **Structured logging throughout the Core library** via optional `ILogger<T>` parameters on all major service classes. Uses `Microsoft.Extensions.Logging.Abstractions` (lightweight, no runtime cost when logging is disabled). All parameters default to `NullLogger<T>.Instance` for full backward compatibility — existing callers require zero code changes.
  - **`LlmsTxtFetcher`** — Logs request URLs, timeout/retry decisions (with attempt counts and backoff delays), WAF detection results (vendor identification), DNS failures, HTTP errors, response body size limit enforcement, and final classification outcomes. Uses `LogDebug` for flow-level decisions, `LogInformation` for successful fetches with metrics (status code, duration, body size), and `LogWarning` for failures (timeouts, DNS, WAF blocks, HTTP errors).
  - **`ContextGenerator`** — Logs section eligibility decisions (Optional section skipping), per-entry fetch results (URL, raw/cleaned sizes), fetch failures with URLs and error messages, token budget application (sections omitted/truncated), and final generation metrics (token count, sections included/omitted/truncated, fetch error count).
  - **`LlmsTxtCache`** — Logs cache hits (in-memory vs. backing store, expired flag), misses, backing store promotion, entry sets with expiry times, backing store persistence, and LRU eviction events (domain evicted, last access time, store size after).
  - **`LlmsDocumentValidator`** — Logs validation start (title, rule count, check options), per-rule issue counts (only when non-zero), and validation summary (isValid, error count, warning count).
  - **`FileCacheBackingStore`** — Logs file save operations (key, path, size), and surfaces previously-silent error paths: corrupted cache files (`JsonException`) and I/O errors (`IOException`) as `LogWarning` entries instead of silently returning null.
  - **`LlmsDocumentParser`** — Accepts optional `ILogger` parameter on the `Parse()` method. When provided, surfaces parse diagnostics (errors and warnings with line numbers) as log entries in addition to including them in the `Diagnostics` collection. Logs parse completion with structural summary (title, section count, entry count, diagnostic count).
- **`Microsoft.Extensions.Logging.Abstractions` NuGet dependency** in `LlmsTxtKit.Core.csproj` (v9.0.0). This is the lightest possible logging abstraction — it provides `ILogger<T>`, `NullLogger<T>`, and structured logging support with zero runtime allocation when logging is disabled. No dependency on any concrete logging provider.
- **DI wiring for loggers** in `ServerConfiguration.cs` — All Core service factory registrations now resolve `ILogger<T>` from DI and pass it through to constructors. The MCP server's existing logging infrastructure (configured to write to stderr) automatically flows through to Core services.
- **40 new unit tests** (total count: 217, up from 177) filling coverage gaps identified in the v0.9.0 audit:
  - **6 parameterized parser tests** (`[Theory]` with `[InlineData]`): Line ending variations (\n, \r\n, mixed), entry URL formats (query strings, fragments), freeform region link handling, H2 trailing whitespace trimming, consecutive blockquote behavior, and Optional section case-sensitivity.
  - **4 cache edge case tests**: TTL=1ms and TTL=0 expiration, LRU eviction with recent-access tracking (verifies the correct entry is evicted), InvalidateAsync idempotency on non-existent domains, and ClearAsync state verification.
  - **5 context generation edge case tests**: Nested HTML comment handling, multiple consecutive comment removal, DefaultTokenEstimator edge cases (empty string, single word, exact multiples, ceiling behavior), all-entries-fail error placeholder verification, and sentence-boundary truncation fallback to word-level truncation.
  - **10 validation rule isolation tests**: BlockquoteMalformedRule (with/without diagnostics), UnexpectedHeadingLevelRule (H3 and H4+, clean documents), ContentOutsideStructureRule (single/multiple orphaned lines, clean structure), network rule skipping verification (`[Theory]` across 3 rule types), and issue severity correctness (`[Theory]` across Error vs. Warning rules).
  - **First use of `[Theory]`/`[InlineData]`** parameterized testing pattern in the project — 15 of the 40 new tests are parameterized, each covering multiple data points per test method.

### Changed

- **Core service constructors now accept optional `ILogger<T>` parameters** — Non-breaking API change. All new parameters are optional with `null` defaults. Existing code that creates Core services without logging continues to work identically. The `LlmsDocumentParser.Parse()` method signature changed from `Parse(string)` to `Parse(string, ILogger?)` — also backward-compatible since the logger parameter is optional.

## [0.9.0] - 2026-02-17

### Added

- **Integration test project** (`LlmsTxtKit.Integration.Tests`) exercising multi-step, cross-component workflows end-to-end. Unlike unit tests which verify individual components in isolation, integration tests validate that the full stack works together: MCP tools → Core services → HTTP layer → JSON serialization. This catches issues like incorrect DI wiring, serialization mismatches, and cross-component state leakage that unit tests miss.
- **`IntegrationMockHandler`** — URL-aware mock `HttpMessageHandler` for integration tests. Delegates to a factory function that inspects the request URL and returns different responses, simulating a realistic web server with multiple endpoints.
- **`IntegrationContentFetcher`** — `IContentFetcher` implementation backed by mock `HttpClient`, bridging the content-fetching interface to the mock HTTP infrastructure so `ContextGenerator` and `FetchSectionTool` share the same mocked HTTP pipeline.
- **7 integration test scenarios** in `EndToEndWorkflowTests.cs`:
  - **End-to-end discovery workflow**: Simulates a 3-step agent interaction (discover → fetch section → generate context) against a realistic llms.txt document with multiple sections. Verifies that steps 2 and 3 use the cache populated by step 1 (`fromCache: true`), confirming zero redundant HTTP requests across the full agent workflow.
  - **WAF block graceful degradation**: Simulates a Cloudflare WAF 403 block (with `cf-ray` and `server: cloudflare` headers) and verifies all four domain-based MCP tools (discover, fetch_section, validate, context) return structured JSON error responses with `success: false` — no unhandled exceptions, no unstructured error strings.
  - **Cache hit within TTL**: Uses a request-counting handler (`Interlocked.Increment`) to verify that a second `Discover` call within TTL returns from cache with zero additional HTTP requests (`requestCount` stays at 1).
  - **Stale-while-revalidate**: Pre-populates the cache with an already-expired entry, then verifies that with SWR enabled, the stale data is still retrievable immediately rather than blocking on a refetch.
  - **Token budget end-to-end**: Generates full context (unlimited), then regenerates with a budget of half the tokens. Asserts `approximateTokenCount <= budget` and at least one section included.
  - **Validation structural checks**: Tests both a well-formed document (`isValid: true`, zero errors) and a malformed document (missing H1 title) through the full Validate tool pipeline, verifying issue structure with severity and rule fields.
  - **Large file handling**: Feeds a synthetic 1000-entry llms.txt (10 sections × 100 entries) through the full pipeline (parse → validate → context with token budget). Verifies correctness and completion within 30-second time bounds. Actual execution: ~26ms — well within bounds.

### Changed

- **Version aligned**: Both `LlmsTxtKit.Core` and `LlmsTxtKit.Mcp` packages now share a unified version number (0.9.0) to reduce confusion when the two packages are installed together. Previously, Core was at 0.5.0 while Mcp was at 0.8.0.
- **User-Agent updated** to `LlmsTxtKit/0.9.0` in `ServerConfiguration.cs` default.

### Verified

- **Documentation completeness audit**: 100% XML documentation coverage on all public types, methods, properties, and interfaces across both Core (35 source files) and Mcp (7 source files). Build with `-warnaserror` produces zero warnings. README covers all 5 MCP tools with usage examples. Design Spec §§ 3.1–3.5 fully documents all tool specifications.
- **NuGet packaging validation**: Both packages pack successfully. `LlmsTxtKit.Core.0.9.0.nupkg` (58 KB) and `LlmsTxtKit.Mcp.0.9.0.nupkg` (37 KB). Known advisory: NU5104 on Mcp package due to prerelease dependency on `ModelContextProtocol 0.8.0-preview.1` — expected until the MCP SDK reaches stable release.
- **Full test suite**: 177 tests passing across 3 projects (97 Core + 73 MCP + 7 Integration). Build time: ~2.5s. Test run time: ~2.5s.

## [0.8.0] - 2026-02-17

### Added

- **`llmstxt_compare` MCP tool** (`Tools/CompareTool.cs`) for HTML vs. Markdown content comparison. Accepts a `url` parameter (specific page URL), fetches both the HTML page at that URL and the Markdown version at `{url}.md` (following the llms.txt convention), and returns comparative metrics. Response includes: `success`, `url`, `markdownUrl`, `markdownAvailable` (boolean), `htmlSize` (bytes), `markdownSize` (bytes, null if unavailable), `sizeRatio` (Markdown/HTML, < 1.0 means Markdown is smaller), `htmlTokenEstimate`, `markdownTokenEstimate`, `tokenRatio` (Markdown/HTML tokens, < 1.0 means fewer tokens), and `freshnessDelta` (Last-Modified difference in seconds; positive = Markdown newer). When no Markdown version exists, returns HTML-only metrics with `markdownAvailable: false`. Designed to support the Context Collapse Mitigation Benchmark study (PRS § 6, Design Spec § 3.5).
- **HTML-to-text stripping utility** (`CompareTool.StripHtmlTags`) that simulates retrieval pipeline HTML processing: removes `<script>` and `<style>` blocks (including content), strips HTML comments, removes all remaining HTML tags, decodes HTML entities (via `WebUtility.HtmlDecode`), and normalizes whitespace. Intentionally simple (regex-based, not a full HTML parser) to match real-world retrieval pipeline behavior.
- **Token estimation utility** (`CompareTool.EstimateTokenCount`) using the same words÷4 ceiling heuristic as `ContextGenerator` for consistency across the toolkit.
- **`CompareTestHandler`** mock HTTP handler for comparison tests — returns different responses for HTML vs. Markdown URLs (based on `.md` suffix), with configurable status codes, content, and Last-Modified headers.
- **18 `CompareTool` unit tests** in `CompareToolTests.cs` covering: both versions available with full metrics, no Markdown version with `markdownAvailable: false`, size and token ratio calculation correctness, freshness delta when Last-Modified headers present, freshness delta null when headers absent, HTML fetch failure error propagation, empty URL validation, invalid URL (non-HTTP scheme) validation, relative URL validation, success response required fields, error response required fields, HTML tag stripping (basic, script/style removal, entity decoding, empty input), token estimation (known input, empty input), and trailing slash URL handling.

### Changed

- **All five MCP tools are now operational** — the full tool suite defined in Design Spec § 3 is complete: `llmstxt_discover`, `llmstxt_fetch_section`, `llmstxt_validate`, `llmstxt_context`, and `llmstxt_compare`. This represents feature completeness for the v1.0 scope's MCP tool layer.

## [0.7.0] - 2026-02-17

### Added

- **`llmstxt_fetch_section` MCP tool** (`Tools/FetchSectionTool.cs`) for retrieving the linked Markdown content of a specific section. Accepts `domain` and `section` parameters (section name is case-insensitive). Fetches each entry's linked URL via the injected `IContentFetcher`, cleans content (strips HTML comments and base64 images via `ContextGenerator.CleanContent`), and returns a JSON response with: `success`, `domain`, `section`, `entryCount`, `entries` array (each with `title`, `url`, `description`, `content` or `error`), `fetchErrors` count, and `fromCache` flag. When the requested section doesn't exist, returns an error with the list of `availableSections` so the agent can self-correct without re-calling `llmstxt_discover`.
- **`llmstxt_validate` MCP tool** (`Tools/ValidateTool.cs`) for running spec-compliance and health checks. Accepts `domain`, optional `check_urls` (boolean, default false), and optional `check_freshness` (boolean, default false). Delegates to `LlmsDocumentValidator` with the appropriate `ValidationOptions`. Returns a JSON response with: `isValid`, `errorCount`, `warningCount`, `issues` array (each with `severity`, `rule`, `message`, `location`), `domain`, `title`, and `fromCache` flag. When the domain's llms.txt cannot be fetched, returns a fetch-level error with `success: false`.
- **`llmstxt_context` MCP tool** (`Tools/ContextTool.cs`) for generating full LLM-ready context strings. Accepts `domain`, optional `max_tokens` (integer, 0 or null = no limit), and optional `include_optional` (boolean, default false). Delegates to `ContextGenerator.GenerateAsync` with `WrapSectionsInXml = true`. Returns a JSON response with: `success`, `domain`, `content` (the generated context string), `approximateTokenCount`, `sectionsIncluded`, `sectionsOmitted` (when budget drops sections), `sectionsTruncated` (when budget truncates), `fetchErrors` (URLs that failed), and `fromCache` flag.
- **`MockContentFetcher`** test infrastructure (`IContentFetcher` mock) supporting both pre-configured successes and explicit failure responses for unit testing tools that fetch linked content.
- **15 `FetchSectionTool` unit tests** in `FetchSectionToolTests.cs` covering: valid section content retrieval, case-insensitive section matching, origin fetch (no cache), cache hit with `fromCache: true`, nonexistent section with `availableSections` hint, partial entry fetch failure with error-per-entry, all-entries-fail scenario, HTML comment stripping in fetched content, domain 404 error propagation, empty domain validation, empty section validation, success response required fields, entry description inclusion, and entry structure verification.
- **15 `ValidateTool` unit tests** in `ValidateToolTests.cs` covering: valid document with `isValid: true` and zero issues, empty issues array on valid doc, no-H1 document with `isValid: false`, empty section warning (document still valid), multiple issues all returned, issue field structure (severity/rule/message), machine-readable rule IDs, origin fetch (no cache), domain 404 error, empty domain validation, whitespace domain validation, success response required fields, error response required fields, and cache hit `fromCache: true`.
- **17 `ContextTool` unit tests** in `ContextToolTests.cs` covering: valid domain context generation with content/tokenCount/sectionsIncluded, Optional section excluded by default, Optional section included when `includeOptional: true`, token budget enforcement (`approximateTokenCount <= maxTokens`), Optional dropped first under budget pressure, `maxTokens=0` treated as unlimited, fetch errors included in response, XML-wrapped content structure, domain 404 error, empty domain validation, whitespace domain validation, success response required fields, error response required fields, and cache hit `fromCache: true`.

### Changed

- **`ContextGenerator.CleanContent`** method visibility changed from `internal` to `public` to allow the MCP tool layer to clean fetched content without duplicating the logic. This is a non-breaking change — the method was already `static` and pure.

## [0.6.0] - 2026-02-17

### Added

- **MCP server infrastructure** in `LlmsTxtKit.Mcp`:
  - `Program.cs` — Complete MCP server entry point using the official C# MCP SDK (`ModelContextProtocol` 0.8.0-preview.1) with `Microsoft.Extensions.Hosting` for DI and lifecycle management. Configures stdio transport (JSON-RPC over stdin/stdout per MCP convention), routes logging to stderr, registers all Core services via DI, and discovers tools by scanning the assembly for `[McpServerToolType]` classes.
  - `Server/ServerConfiguration.cs` — DI service registration extension method (`AddLlmsTxtKitServices`) that wires up all five Core services as singletons: `LlmsTxtFetcher`, `LlmsTxtCache`, `LlmsDocumentValidator`, `ContextGenerator`, and `HttpContentFetcher`. Configuration is read from environment variables (`LLMSTXTKIT_USER_AGENT`, `LLMSTXTKIT_TIMEOUT_SECONDS`, `LLMSTXTKIT_MAX_RETRIES`, `LLMSTXTKIT_CACHE_TTL_MINUTES`, `LLMSTXTKIT_CACHE_MAX_ENTRIES`, `LLMSTXTKIT_CACHE_DIR`) with sensible defaults.
  - `Tools/DiscoverTool.cs` — First MCP tool: `llmstxt_discover`. Accepts a `domain` parameter, checks for `/llms.txt` via `LlmsTxtFetcher`, caches successful results in `LlmsTxtCache`, and returns a JSON response containing: `found` (boolean), `status` (success/notFound/blocked/rateLimited/dnsFailure/timeout/error), and on success: `title`, `summary`, `sections` (array of `{ name, entryCount, isOptional }`), `diagnostics` (parse warnings/errors), and `fromCache` flag. Handles all seven `FetchStatus` outcomes with human-readable error messages. Includes comprehensive debug logging at every decision point.
- **NuGet package dependencies** for `LlmsTxtKit.Mcp`:
  - `ModelContextProtocol` 0.8.0-preview.1 (official C# MCP SDK, maintained in collaboration between Anthropic and Microsoft)
  - `Microsoft.Extensions.Hosting` 10.0.3 (DI container, configuration, and host lifecycle)
  - `Microsoft.Extensions.Logging.Console` 10.0.3 (console logging provider for stderr output)
- **`InternalsVisibleTo`** attribute in `LlmsTxtKit.Mcp.csproj` exposing internal response DTOs to the test project for JSON structure assertions.
- **14 MCP tool unit tests** in `DiscoverToolTests.cs` covering: successful discovery with full response structure validation, cache population on first fetch, cache hit returning `fromCache: true`, HTTP 404 not-found response, Cloudflare WAF block with `blockReason`, AWS CloudFront WAF block, 429 rate limiting, DNS failure, 500 server error, empty domain parameter validation, whitespace domain parameter validation, success response required fields check, error response required fields check, and parse diagnostics inclusion.
- **`MockHttpHandler`** test infrastructure in `LlmsTxtKit.Mcp.Tests` for simulating HTTP responses in MCP tool tests without network access.

## [0.5.0] - 2026-02-17

### Added

- **Context generation types** in `LlmsTxtKit.Core.Context`:
  - `ContextOptions` — Configuration class: `MaxTokens` (nullable, no limit by default), `IncludeOptional` (default `false`, matching reference implementation's `create_ctx(optional=False)`), `WrapSectionsInXml` (default `true`), `TokenEstimator` (nullable custom `Func<string, int>`, defaults to words÷4 heuristic).
  - `ContextResult` — Immutable result containing generated `Content` string, `ApproximateTokenCount`, `SectionsIncluded`, `SectionsOmitted`, `SectionsTruncated`, and `FetchErrors` lists.
  - `FetchError` — Immutable record pairing a `Uri` with an error message for URLs that failed during context generation.
  - `IContentFetcher` — Interface abstracting linked content retrieval for testability. Returns `ContentFetchResult` with content or error message.
  - `HttpContentFetcher` — Default `IContentFetcher` implementation using `HttpClient` with 15-second timeout and structured error handling.
  - `ContextGenerator` — Service class that fetches linked Markdown content, cleans it (strips HTML comments and base64 images), applies token budgets with graceful truncation at sentence boundaries, wraps sections in `<section name="...">` XML tags, respects Optional section semantics, and includes error placeholders for failed fetches.
- **Token budgeting algorithm**: Drops Optional sections first, then truncates remaining sections last-to-first at sentence boundaries. Appends truncation indicator when content is cut. Default estimator uses words÷4 heuristic; custom estimators supported via `Func<string, int>`.
- **17 context generation unit tests** in `ContextTests.cs` covering: single/multiple section generation, Optional section exclusion by default and inclusion when requested, XML wrapping enabled/disabled, token budget with Optional-dropped-first priority, sentence-boundary truncation, truncation indicator presence, fetch failure error placeholders, custom token estimator usage, HTML comment stripping, base64 image stripping, default token estimator accuracy, null input validation, and positive token count verification.

### Changed

- **LlmsTxtKit.Core library is now feature-complete** for the v1.0 scope. All 5 Core components (Parsing, Fetching, Validation, Caching, Context Generation) are implemented with full test coverage.

## [0.4.0] - 2026-02-17

### Added

- **Caching types** in `LlmsTxtKit.Core.Caching`:
  - `CacheEntry` — Immutable class storing a parsed `LlmsDocument`, optional `ValidationReport`, fetch/expiry timestamps, HTTP headers (ETag, Last-Modified), full `FetchResult`, and LRU access tracking.
  - `CacheOptions` — Configuration class: `DefaultTtl` (default 1 hour), `MaxEntries` (default 1000), `StaleWhileRevalidate` (default `true`), `BackingStore` (nullable for optional persistence).
  - `ICacheBackingStore` — Interface for pluggable persistent storage with `SaveAsync`, `LoadAsync`, `RemoveAsync`, and `ClearAsync` methods.
  - `FileCacheBackingStore` — File-backed implementation serializing cache entries to individual JSON files with atomic write (temp + rename) pattern. Uses a DTO layer that re-parses raw content through `LlmsDocumentParser.Parse` on load.
  - `LlmsTxtCache` — Domain-keyed cache service with `GetAsync`, `SetAsync`, `InvalidateAsync`, and `ClearAsync` methods. Features thread-safe `ConcurrentDictionary` storage, LRU eviction on capacity overflow, stale-while-revalidate for expired entries, case-insensitive domain lookup, and write-through backing store integration.
- **`InternalsVisibleTo`** attribute in `LlmsTxtKit.Core.csproj` exposing internal members to the test project for thorough unit testing of LRU timestamps.
- **22 cache unit tests** in `CacheTests.cs` covering: cache hit/miss, case-insensitive lookup, expired entry with stale-while-revalidate enabled/disabled, LRU eviction when `MaxEntries` is exceeded, invalidation and clear operations, `FileCacheBackingStore` round-trip serialization, backing store fallback with in-memory promotion, write-through persistence, concurrent read/write thread safety, and null input validation.

## [0.3.0] - 2026-02-17

### Added

- **Validation types** in `LlmsTxtKit.Core.Validation`:
  - `ValidationSeverity` — Enum: `Warning`, `Error`. Aligns with the two-tier severity model from Design Spec § 2.3.
  - `ValidationIssue` — Immutable record containing severity, rule code, human-readable message, and optional location string.
  - `ValidationReport` — Aggregation class with `AllIssues` (sorted errors-first), computed `IsValid` (true when zero errors), `Errors`, `Warnings`, and static `Empty` factory.
  - `ValidationOptions` — Configuration class: `CheckLinkedUrls` (default `false`), `CheckFreshness` (default `false`), `UrlCheckTimeout` (default 10 seconds).
  - `IValidationRule` — Interface for pluggable validation rules with `EvaluateAsync(LlmsDocument, ValidationOptions, CancellationToken)`.
- **10 built-in validation rules** in `LlmsTxtKit.Core.Validation.Rules`:
  - `RequiredH1MissingRule` — Error: document has no H1 title.
  - `MultipleH1FoundRule` — Error: document has more than one H1 heading.
  - `EntryUrlInvalidRule` — Error: entry URL uses a non-HTTP(S) scheme or is malformed.
  - `BlockquoteMalformedRule` — Warning: blockquote/summary has formatting issues (bridges parser diagnostics).
  - `SectionEmptyRule` — Warning: H2 section contains zero entries.
  - `UnexpectedHeadingLevelRule` — Warning: H3+ headings found (llms.txt spec only defines H1/H2).
  - `ContentOutsideStructureRule` — Warning: non-entry text found inside a section (orphaned content).
  - `EntryUrlUnreachableRule` — Warning (network-dependent): linked URL returns non-2xx status. Only runs when `CheckLinkedUrls = true`.
  - `EntryUrlRedirectedRule` — Warning (network-dependent): linked URL returns 3xx redirect. Only runs when `CheckLinkedUrls = true`.
  - `ContentStaleRule` — Warning (network-dependent): linked URL's `Last-Modified` header indicates content older than 365 days. Only runs when `CheckFreshness = true`.
- **`LlmsDocumentValidator`** orchestrator that registers all 10 built-in rules, supports custom rules via `AddRule()`, and produces a sorted `ValidationReport`.
- **22 validator unit tests** in `ValidatorTests.cs` covering: per-rule positive and negative cases for all structural rules, network-dependent rule skip-when-disabled verification, orchestrator-level tests for `IsValid`, multiple-issue aggregation, warnings-only validity, severity sorting (errors before warnings), and null-document exception handling.

## [0.2.0] - 2026-02-17

### Added

- **Fetching types** in `LlmsTxtKit.Core.Fetching`:
  - `FetchStatus` — Enum with 7 outcome categories: `Success`, `NotFound`, `Blocked`, `RateLimited`, `DnsFailure`, `Timeout`, `Error`. Represents the distinct decisions a caller needs to make based on the fetch outcome.
  - `FetchResult` — Immutable result type containing the classified status, parsed document (on success), raw content, HTTP status code, response headers, WAF block reason, Retry-After duration, error message, fetch duration, and domain name.
  - `FetcherOptions` — Configuration class for the fetcher: `UserAgent`, `TimeoutSeconds` (default 15), `MaxRetries` (default 2), `RetryDelayMs` (default 1000), `AcceptHeaderOverride`, `MaxResponseSizeBytes` (default 5 MB for SEC-3 compliance).
  - `LlmsTxtFetcher` — Service class that fetches llms.txt files by domain name with WAF block detection (Cloudflare, AWS CloudFront, Akamai), retry logic with exponential backoff and jitter, per-request timeout handling, response size limiting, and `IDisposable` lifecycle management.
- **WAF detection heuristics**: Cloudflare (`cf-ray`, `server: cloudflare`, challenge page body patterns), AWS CloudFront/WAF (`server: CloudFront`, `x-amzn-waf-action`), Akamai (`server: AkamaiGHost`, `x-akamai-transformed`).
- **`MockHttpHandler`** reusable test infrastructure in `LlmsTxtKit.Core.Tests` for unit testing HTTP-dependent code without live network calls.
- **20 fetcher unit tests** in `FetcherTests.cs` covering: success parsing, 404 handling, Cloudflare WAF detection, generic 403 handling, 429 rate limiting with Retry-After parsing, DNS failure, timeout, 500 server error, retry with eventual success, cancellation token propagation, null/empty domain validation, custom User-Agent propagation, duration tracking on success and failure, URL construction verification, non-transient error non-retry, response header capture, AWS WAF detection, and disposed fetcher behavior.

## [0.1.0] - 2026-02-17

### Added

- **Parsing types** in `LlmsTxtKit.Core.Parsing`:
  - `LlmsDocument` — Immutable record representing a fully parsed llms.txt document.
  - `LlmsSection` — Immutable record for H2-delimited sections with entries and `IsOptional` flag.
  - `LlmsEntry` — Immutable record for link entries with URL, title, and description.
  - `ParseDiagnostic` — Structured diagnostic record with severity, message, and line number.
  - `DiagnosticSeverity` — Enum: `Warning`, `Error`.
  - `LlmsDocumentParser` — Static parser producing `LlmsDocument` from raw llms.txt content with regex-based line processing aligned to the reference Python `miniparse.py` implementation.
- **Test data corpus** in `tests/TestData/`:
  - `valid/`: anthropic.txt, fasthtml.txt, minimal.txt, full-featured.txt.
  - `malformed/`: no-title.txt, missing-urls.txt.
  - `edge-cases/`: unicode.txt, empty-sections.txt.
- **16 parser unit tests** in `ParserTests.cs` covering happy path, edge cases, and malformed input.
- Repository scaffolding: README, LICENSE, CONTRIBUTING, CHANGELOG, solution file, project structure.
- Specification documents: PRS, Design Spec, User Stories, Test Plan (in `specs/`).
