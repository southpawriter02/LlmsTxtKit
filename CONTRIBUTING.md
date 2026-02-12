# Contributing to LlmsTxtKit

Thank you for your interest in contributing to LlmsTxtKit. This document covers development setup, coding standards, and pull request guidelines.

---

## Development Setup

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- A C# IDE (Visual Studio 2022, Visual Studio Code with the C# extension, or JetBrains Rider)
- Git

### Getting Started

```bash
# Clone the repository
git clone https://github.com/YOUR_USERNAME/LlmsTxtKit.git
cd LlmsTxtKit

# Restore dependencies and build
dotnet restore
dotnet build

# Run the test suite
dotnet test
```

---

## Coding Standards

### C# Conventions

LlmsTxtKit follows standard .NET coding conventions with these specific practices:

- **Nullable reference types** are enabled project-wide. All reference type parameters, return values, and properties must be explicitly annotated as nullable (`?`) or non-nullable.
- **XML documentation comments** are required on every public type, method, property, and parameter. The build treats missing XML docs as warnings. These comments are used to generate the API reference documentation.
- **File-scoped namespaces** (`namespace LlmsTxtKit.Core.Parsing;`) rather than block-scoped.
- **Primary constructors** are preferred where they improve readability, but traditional constructors are acceptable for complex initialization logic.
- **`required` properties** are used for types where construction without essential data should be a compile-time error.

### Naming Conventions

| Element | Convention | Example |
|---|---|---|
| Classes, records, interfaces | PascalCase | `LlmsDocument`, `ILlmsTxtFetcher` |
| Public methods and properties | PascalCase | `ParseAsync`, `ValidationReport` |
| Private fields | `_camelCase` with underscore prefix | `_httpClient`, `_cacheEntries` |
| Local variables and parameters | camelCase | `fetchResult`, `sectionName` |
| Constants | PascalCase | `DefaultTimeoutSeconds` |
| Enum values | PascalCase | `FetchStatus.Blocked` |

### Testing Conventions

- Tests use [xUnit](https://xunit.net/).
- Test classes mirror the source class name with a `Tests` suffix: `LlmsDocumentParser` → `LlmsDocumentParserTests`.
- Individual test methods follow the pattern `MethodName_Scenario_ExpectedBehavior`:
  ```csharp
  [Fact]
  public void Parse_MissingH1Title_ThrowsParseException()
  ```
- Use `[Fact]` for single-case tests and `[Theory]` with `[InlineData]` or `[MemberData]` for parameterized tests.
- Integration tests that require HTTP are in separate test classes and use a mock HTTP server (not live network calls).

---

## Documentation-First Workflow

LlmsTxtKit follows a strict documentation-first methodology. The expected workflow for any significant feature or change is:

1. **Update the spec** (in `specs/`) if the change affects the product requirements, architecture, or user stories.
2. **Update or add user stories** if the change adds or modifies a user-facing capability.
3. **Update or add tests** that validate the expected behavior before writing implementation code.
4. **Implement the change.**
5. **Update XML doc comments** on any new or modified public API surface.
6. **Update the CHANGELOG** under the `[Unreleased]` section.

---

## Pull Request Guidelines

### Before Submitting

- Ensure `dotnet build` completes with no errors and no new warnings.
- Ensure `dotnet test` passes all existing and new tests.
- Run `dotnet format` to ensure consistent code style.
- Update the CHANGELOG under `[Unreleased]` with a description of your change.

### PR Description

Please include:

- **What** the change does (one sentence).
- **Why** the change is needed (motivation, issue number if applicable).
- **How** it works at a high level (if the implementation isn't obvious from the diff).
- **Testing** — what tests were added or modified, and what scenarios they cover.

### Review Process

All PRs require at least one review before merging. Reviewers will check:

- Code correctness and test coverage.
- XML documentation completeness on public API surface.
- Consistency with existing coding standards and architectural patterns.
- Whether the CHANGELOG was updated.

---

## What to Contribute

The most impactful contributions right now are described in the [README](README.md#contributing):

- **Test data** — Real-world llms.txt files for the test corpus.
- **Infrastructure scenarios** — WAF blocking patterns, rate limiting behaviors, redirect chains for integration tests.
- **Spec interpretation questions** — Ambiguities in the llms.txt spec that affect parser behavior.

---

## Questions?

If you're unsure about anything — whether a change fits the project direction, how to structure a test, or whether a spec ambiguity should be an issue or a PR — open a GitHub Discussion. We'd rather help you contribute effectively than lose a good contribution to process confusion.
