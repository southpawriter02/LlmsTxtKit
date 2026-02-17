# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

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
