# Test Plan

**Status:** 🔲 Not started
**Target completion:** Phase 1 (Weeks 3–4)

---

> This document will define unit test coverage targets, integration test scenarios, and the test data corpus for LlmsTxtKit. It answers the question: *how do we know it works?*
>
> For the testing strategy overview, see the [project proposal](https://github.com/southpawriter02/llmstxt-research/blob/main/PROPOSAL.md) (Project 2 § Testing Strategy).
> For testing conventions and tooling, see [CONTRIBUTING.md](../CONTRIBUTING.md) § Testing Conventions.

<!-- TODO: Write test plan content. Sections to include:

  1. Coverage Targets
     - Unit test coverage goals per component (Parser, Fetcher, Validator, Cache, Context)
     - Integration test scenario count targets

  2. Unit Test Scenarios
     - Parser: spec-compliant inputs, edge cases (missing sections, malformed links,
       Unicode, very large files, empty files)
     - Validator: per-rule testing (H1 required, blockquote structure, link list validity,
       URL resolution, freshness checks)
     - Fetcher: not tested via live HTTP — uses mock HTTP handler
     - Cache: TTL behavior, eviction, serialization
     - Context: section expansion, Optional section handling, token budgeting

  3. Integration Test Scenarios (mock HTTP server)
     - Healthy llms.txt (200 OK, valid content)
     - 403 Forbidden (WAF block)
     - 429 Too Many Requests (rate limiting)
     - 404 Not Found
     - Redirect chains (301 → 301 → 200)
     - Slow responses (timeout handling)
     - Malformed content (invalid Markdown, binary data)

  4. MCP Protocol Tests
     - Each tool returns correctly structured MCP responses
     - Error cases produce valid MCP error responses
     - Tool descriptions and parameter schemas are spec-compliant

  5. Test Data Corpus
     - tests/TestData/valid/ — Real-world llms.txt files from Anthropic, Cloudflare,
       Stripe, FastHTML, etc.
     - tests/TestData/malformed/ — Intentionally broken files
     - tests/TestData/edge-cases/ — Unicode, very large, empty, etc.

  6. Continuous Integration
     - All tests run on every PR via GitHub Actions
     - Coverage report generated and tracked
-->
