# Product Requirements Specification (PRS)

**Author:** Ryan
**Version:** 1.0
**Last Updated:** February 2026
**Status:** Draft

---

## 1. Problem Statement

The `/llms.txt` standard was designed to help AI systems discover and retrieve clean, curated Markdown content from websites at inference time. Over 844,000 websites have adopted some form of the standard, including Anthropic, Cloudflare, Stripe, and Vercel. The developer documentation sector has embraced it enthusiastically.

But two concrete problems prevent most .NET developers from working with llms.txt programmatically:

**Problem 1: There is no .NET tooling.** Every existing llms.txt implementation is written in Python or JavaScript. The official `llms_txt` Python module handles parsing and context generation. A JavaScript implementation mirrors it. Docusaurus and VitePress plugins generate llms.txt files from documentation sites. PHP and Drupal implementations exist. On NuGet, there are zero packages. A .NET developer building an AI-integrated application who wants to parse, fetch, validate, or generate context from llms.txt files has no library to reach for — they must write everything from scratch or shell out to another runtime.

**Problem 2: The web's security infrastructure makes fetching unreliable.** Roughly 20% of public websites sit behind Cloudflare, which began blocking AI crawlers by default on new domains in July 2025. AI-style HTTP requests — those without JavaScript execution, without cookies, from data center IPs, with non-browser user agents — trigger Web Application Firewall (WAF) heuristics. Even when a site owner explicitly wants AI systems to access their llms.txt file, the hosting infrastructure may prevent it. Existing tools do not handle this gracefully. They either succeed or throw an exception, with no structured information about *why* the fetch failed or what the caller should do about it.

These two problems compound each other. A .NET developer who *also* needs to handle WAF-blocked fetches, stale content, broken links, and the llms.txt spec's structural ambiguities currently has no path forward that doesn't involve significant custom engineering.

---

## 2. Target Users

LlmsTxtKit serves two distinct user types with different interaction models:

### 2.1 Library Consumers (.NET Developers)

**Who they are:** Developers building AI-integrated .NET applications — ASP.NET Core web services, Azure Functions, Blazor apps, console tools, or any C# codebase that needs to interact with the llms.txt ecosystem.

**What they need:** A NuGet package they can `dotnet add` and call from C# code. Strongly-typed object models (not raw strings). Async APIs that follow .NET conventions. Configurable behavior (timeouts, retry counts, cache TTL) via options objects, not magic strings. XML documentation on every public type and method so IntelliSense works properly. A library that handles infrastructure-level failures gracefully, reporting what happened rather than crashing.

**How they interact with LlmsTxtKit:** They reference `LlmsTxtKit.Core` as a NuGet dependency and call its APIs directly from their code. They configure behavior through options objects passed to constructors. They receive results as strongly-typed return values (`LlmsDocument`, `FetchResult`, `ValidationReport`, etc.).

**Example scenarios:**
- A developer building a documentation search tool wants to parse llms.txt files from multiple sites and index the linked content.
- A developer building an AI chatbot wants to fetch llms.txt at inference time to provide curated context for a specific domain.
- A documentation team wants to validate their own llms.txt file as part of a CI/CD pipeline to catch structural errors before deployment.
- A researcher (the benchmark study author) wants to compare HTML versus Markdown versions of the same content across many sites.

### 2.2 MCP Agent Consumers (AI Agents)

**Who they are:** AI systems operating via the Model Context Protocol — Claude, GitHub Copilot, or custom agents built on the MCP standard. These agents don't write C# code; they discover and invoke tools by reading tool descriptions and schemas.

**What they need:** Well-named, well-described MCP tools with clear parameter schemas and structured response formats. Tools that expose the right level of granularity: not too low-level (an agent shouldn't need to understand caching internals) and not too high-level (an agent should be able to fetch a specific section without retrieving everything).

**How they interact with LlmsTxtKit:** They connect to a running `LlmsTxtKit.Mcp` server process and invoke tools by name (`llmstxt_discover`, `llmstxt_fetch_section`, etc.). They receive structured JSON responses that they can reason about and incorporate into their outputs.

**Example scenarios:**
- An agent receives a user question about a specific library and uses `llmstxt_discover` to check whether the library's documentation site has an llms.txt file, then `llmstxt_context` to retrieve curated content for context injection.
- An agent is asked to audit a website's AI discoverability and uses `llmstxt_validate` to check structural compliance and link health.
- An agent building a comparison of different documentation sites uses `llmstxt_compare` to measure the size and freshness delta between HTML and Markdown versions.

---

## 3. Success Criteria

LlmsTxtKit v1.0 will be considered successful when it meets the following criteria:

### 3.1 Functional Completeness

**SC-1: Spec-compliant parsing.** The parser correctly handles all well-formed llms.txt files that conform to the specification at llmstxt.org. The canonical behavioral reference is the official Python implementation in [`AnswerDotAI/llms-txt`](https://github.com/AnswerDotAI/llms-txt) (`miniparse.py` and `core.py`), which defines the ground-truth parsing behavior that this library must be compatible with. Specifically:
- Extracts the required H1 title.
- Extracts the optional blockquote summary. (Note: the reference parser captures a single-line blockquote only. LlmsTxtKit should handle multi-line blockquotes gracefully as a superset, but single-line is the baseline expectation.)
- Extracts zero or more freeform Markdown content (the reference implementation calls this the "info" field) between the summary and the first H2 — paragraphs, lists, inline formatting, but not headings.
- Extracts zero or more H2-delimited sections containing file lists (Markdown links with optional colon-separated descriptions). H3 and deeper headings are treated as content within the parent H2 section, not as structural delimiters — this matches the reference parser's `^##\s*` split behavior.
- Identifies the `## Optional` section (case-sensitive, exact match — matching the reference implementation's `k != 'Optional'` check) and marks its entries accordingly.
- Produces meaningful error information for files that don't conform to the spec. (The reference implementation does not validate or produce diagnostics — structured error reporting is LlmsTxtKit's value-add beyond the reference.)

**SC-2: Resilient fetching.** The fetcher reliably retrieves llms.txt files from the open web, correctly distinguishing between seven outcome categories: Success, NotFound, Blocked (WAF/CDN), RateLimited, DnsFailure, Timeout, and Error. For blocked requests, the `FetchResult` includes diagnostic information (HTTP status code, response headers, block reason where detectable).

**SC-3: Spec-aware validation.** The validator checks at least the following rules:
- Required H1 title is present.
- Blockquote summary structure is correct (if present).
- H2 sections contain valid Markdown link lists.
- No content appears outside the defined structural elements.
- Linked URLs resolve (when `CheckLinkedUrls` is enabled).
- Last-Modified freshness comparison (when `CheckFreshness` is enabled).

**SC-4: Configurable caching.** The cache stores parsed documents with configurable TTL, supports in-memory storage (default) and optionally file-backed storage, and correctly evicts stale entries. Cache entries include the parsed document, validation report, fetch timestamp, and relevant HTTP headers.

**SC-5: Context generation with token budgeting.** The context generator fetches linked Markdown files, wraps them in XML-style section markers, respects the Optional section's semantics, and truncates gracefully when a token budget is specified. The reference implementation's `create_ctx()` function defaults to `optional=False` (Optional sections excluded by default), and LlmsTxtKit follows this convention — `IncludeOptional` defaults to `false`. The reference implementation also outputs XML using a `<project>/<section>/<doc>` element hierarchy (via fastcore's FT system); LlmsTxtKit uses a `<section name="...">` generic wrapper approach instead, which is a deliberate design divergence documented in the Design Spec §2.5.

**SC-6: Five MCP tools operational.** The MCP server exposes all five documented tools (`llmstxt_discover`, `llmstxt_fetch_section`, `llmstxt_validate`, `llmstxt_context`, `llmstxt_compare`) with correctly structured MCP tool definitions, parameter schemas, and response formats.

### 3.2 Quality Standards

**SC-7: XML documentation coverage.** Every public type, method, property, and parameter in LlmsTxtKit.Core has XML documentation comments. The build treats missing XML docs as warnings.

**SC-8: Test coverage.** Unit tests cover the parser (spec-compliant inputs, edge cases, malformed inputs), validator (each rule independently), fetcher (mock HTTP scenarios), cache (TTL, eviction), and context generator (section expansion, Optional handling, token budgeting). Integration tests cover mock HTTP scenarios (WAF block, rate limit, redirect chains, timeouts). MCP protocol tests verify tool response structure.

**SC-9: Real-world test corpus.** The test suite includes a curated corpus of llms.txt files sourced from real production websites (Anthropic, Cloudflare, Stripe, FastHTML, etc.) to verify that the parser handles actual content correctly, not just synthetic test cases.

### 3.3 Ecosystem Fit

**SC-10: NuGet publishable.** Both packages build cleanly and produce valid NuGet packages with correct metadata (package ID, version, description, license, repository URL, tags).

**SC-11: Idiomatic .NET.** The API follows standard .NET conventions: async methods return `Task<T>`, cancellation tokens are accepted on async operations, options objects configure behavior, nullable annotations are consistent, and dependency injection is supported for the MCP server's service registration.

---

## 4. Non-Goals

These items are explicitly **out of scope** for LlmsTxtKit v1.0. Some may become post-v1.0 features if community demand warrants them, but they are not being designed for, not being tested for, and not being promised.

**NG-1: Generating llms.txt files.** LlmsTxtKit *consumes* llms.txt files; it does not *produce* them. Generating an llms.txt file from a documentation site's content is a different problem (solved by the Docusaurus and VitePress plugins) that requires deep integration with the site's content pipeline. It is not a retrieval problem.

**NG-2: Hosting a public service.** LlmsTxtKit is a library and a self-hosted MCP server. It is not a SaaS product, a hosted API, or a public web service. Users run it in their own infrastructure.

**NG-3: Supporting .NET versions below 8.0.** The library targets `net8.0` exclusively. .NET 6 is out of Long-Term Support (ended November 2024). .NET 7 was never an LTS release. Older frameworks (.NET Framework 4.x, .NET Standard) are not targeted. See the [Unified Roadmap](https://github.com/southpawriter02/llmstxt-research/blob/main/ROADMAP.md) § Tech Stack Rationale for the full decision rationale.

**NG-4: Content Signals, CC Signals, or IETF aipref support.** These are complementary standards that govern permission and usage rights, not content discovery. They are architecturally distinct from llms.txt and would require different parsing, validation, and response models. They may become post-v1.0 extensions if the ecosystem converges.

**NG-5: Bypassing WAF protections.** LlmsTxtKit's fetcher *detects and reports* WAF blocking. It does not attempt to circumvent it. No CAPTCHA solving, no JavaScript rendering, no headless browser automation, no user-agent spoofing to impersonate real browsers. The library respects the web's security infrastructure and provides diagnostic information so that *humans* can reconfigure their WAF settings if they want AI access.

**NG-6: Token counting with model-specific tokenizers.** The context generator's `MaxTokens` budget uses an approximate token count (word-based heuristic or a general-purpose tokenizer like tiktoken). It does not ship with model-specific tokenizers for every LLM family. Callers who need precise token counts for a specific model should apply their own tokenizer to the generated context string.

**NG-7: Real-time content synchronization.** The library fetches and caches. It does not subscribe to webhooks, poll for changes, or maintain a real-time mirror of remote content. Cache staleness is managed through TTL expiration, not push-based updates.

---

## 5. Assumptions and Constraints

### 5.1 Assumptions

**A-1: The llms.txt spec remains stable.** The specification at llmstxt.org has been stable since its September 2024 publication, and the reference Python implementation at `AnswerDotAI/llms-txt` serves as the canonical behavioral baseline. The parser is designed against both the spec text and the reference implementation's behavior. If the spec or reference implementation changes substantially, the parser will need updating. Minor clarifications (resolving ambiguities in link format, Optional section behavior, etc.) are expected and the parser should handle them gracefully.

**A-2: MCP protocol stability.** The Model Context Protocol specification is maintained by Anthropic and is under active development. LlmsTxtKit.Mcp targets the current stable MCP protocol version. If the protocol changes, the MCP server may need updating, but LlmsTxtKit.Core (the library) is entirely protocol-agnostic and unaffected.

**A-3: HttpClient is the right HTTP abstraction.** .NET's `HttpClient` (via `IHttpClientFactory` for proper lifecycle management) is the standard HTTP abstraction in the .NET ecosystem. LlmsTxtKit uses it for all HTTP operations. If an exotic HTTP scenario requires a different transport (e.g., HTTP/3 for specific CDNs), this can be accommodated through `HttpMessageHandler` customization without changing LlmsTxtKit's API surface.

### 5.2 Constraints

**C-1: No runtime dependencies on Python or JavaScript.** LlmsTxtKit is a pure .NET library. It does not shell out to Python, does not require Node.js, and does not use any cross-runtime interop.

**C-2: No external service dependencies.** LlmsTxtKit.Core operates entirely locally. It makes outbound HTTP requests to fetch llms.txt files, but it does not depend on any external API, registry, or authentication service. If the network is unavailable, the cache serves stale entries (within configurable tolerance) and fetches fail with structured error information.

**C-3: Single-developer maintenance capacity.** The project is maintained by a single author. API surface, feature scope, and post-v1.0 commitments are scoped accordingly. Community contributions are welcome but not assumed for the v1.0 timeline.

---

## 6. Relationship to Other Projects

LlmsTxtKit is the practical implementation arm of the broader [llms.txt Research & Tooling Initiative](https://github.com/southpawriter02/llmstxt-research). Specifically:

- The analytical paper ("The llms.txt Access Paradox") documents the infrastructure problems that LlmsTxtKit is designed to handle. The paper says "here's why direct fetching fails." LlmsTxtKit's `FetchResult` status codes and WAF-aware retry logic are the engineering response.

- The benchmark study ("Context Collapse Mitigation Benchmark") uses LlmsTxtKit's `llmstxt_compare` tool as its primary data collection instrument. The paired HTML-vs-Markdown data that forms the study's experimental input is generated by this library.

- The blog series translates LlmsTxtKit's design decisions and implementation lessons into practitioner guidance (Blog Post 3: ".NET ecosystem gap"; Blog Post 7: "MCP C# tutorial").

However, LlmsTxtKit is designed to be useful on its own merits, entirely independently of the research initiative. A .NET developer can install the NuGet package and use it without ever reading the paper or caring about the benchmark. The library's value proposition stands alone: it is the only .NET package for working with llms.txt files.

---

## Revision History

| Version | Date | Changes |
|---|---|---|
| 1.0 | February 2026 | Initial PRS |
