# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

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
