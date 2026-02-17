# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

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
