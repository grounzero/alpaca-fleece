# Python Development Standards

**AGENT-ONLY — Do not include in project files**

These standards apply to all Python development work. They are designed to be transferable across projects.

## 1) Style and Formatting

- Follow PEP 8 by default
- Use an auto-formatter: **black** (no arguments; make it the source of truth)
- Sort imports with **isort** (configured to match Black)
- Lint with **ruff** (fast, covers many rules)
- Line length: 88 (Black default)
- Indentation: 4 spaces; never tabs
- Quotes: don't bikeshed—let Black decide; be consistent in docstrings

## 2) Naming Conventions

- **Modules/files**: `lowercase_with_underscores.py`
- **Packages**: `lowercase` (avoid underscores if possible)
- **Classes**: `PascalCase`
- **Functions/variables**: `snake_case`
- **Constants**: `UPPER_SNAKE_CASE`
- **Private/internal**: prefix with `_` (and don't export it from package APIs)

## 3) Project Structure

- Prefer a `src/` layout for installable packages:
  ```
  src/<package_name>/...
  tests/
  ```
- Each package should have:
  - `pyproject.toml` (single config hub)
  - `README.md`
  - `LICENSE`
- Keep "scripts" separate from library code:
  - `src/<pkg>/` contains reusable logic
  - `scripts/` contains entry scripts / one-offs

## 4) Type Hints (Required)

- Type-hint all public functions and any non-trivial internal functions
- Use modern typing: `list[str]`, `dict[str, int]`, `X | None`
- Avoid `Any` unless you have a good reason; document it when used
- Run **mypy** or **pyright** in CI (pick one)

## 5) Docstrings and Documentation

- Docstrings required for public modules, classes, and functions
- Use a consistent style (pick one):
  - Google style, or
  - NumPy style
- Docstrings should explain:
  - purpose
  - parameters/return (especially edge cases)
  - exceptions (only if relevant)
- Comments should explain **why**, not what (the code shows what)

## 6) Imports

- Imports grouped as:
  1. standard library
  2. third-party
  3. local app imports
- No wildcard imports (`from x import *`)
- Avoid deep relative imports; prefer absolute imports in packages

## 7) Error Handling and Exceptions

- Never use bare `except:` (use `except Exception:` at minimum)
- Catch exceptions only when you can:
  - add context,
  - recover,
  - or translate into a domain-specific error
- Create custom exception types for your domain boundaries (API/service layer)
- Don't swallow errors; log them with context

## 8) Logging

- Use the `logging` module, not `print`
- Libraries should never configure global logging; apps can
- Log with structured context:
  - good: `logger.info("user_created", extra={"user_id": user_id})` (or equivalent)
- Don't log secrets (tokens, passwords, raw PII)

## 9) Testing Standards

- Use **pytest**
- Tests should be:
  - deterministic
  - isolated (no shared global state)
  - fast (mock external I/O)
- Minimum expectations:
  - unit tests for core logic
  - integration tests for key flows (DB/API) behind a marker
- Use fixtures thoughtfully; avoid fixture "magic" that hides too much

## 10) API and Design Rules

- Keep functions small and single-purpose
- Prefer pure functions where possible (easier to test)
- Avoid unnecessary cleverness:
  - clarity > brevity
- Use dataclasses for simple models; pydantic if validation is needed
- Be explicit at module boundaries: validate inputs, normalize outputs

## 11) Performance and Correctness

- Don't optimize prematurely—but:
  - avoid O(n²) patterns in obvious hotspots,
  - prefer generators/iterators for large streams,
  - use `pathlib` for paths
- If performance matters, measure with timeit/profilers before changing code

## 12) Security and Secrets

- No secrets in code, logs, or committed configs
- Use environment variables or a secrets manager
- Treat user input as hostile:
  - validate and sanitize,
  - use parameterized SQL (never string formatting),
  - avoid eval/exec

## 13) Tooling and CI Baseline (Recommended)

Pre-commit hooks:
- ruff (lint + fixes)
- black
- isort
- mypy/pyright
- pytest

CI should run:
- lint
- type check
- tests
- (optional) coverage threshold

## 14) "Definition of Done" for Python Changes

A PR is done when:
- [ ] formatting/lint/type-check pass locally and in CI
- [ ] new behaviour has tests
- [ ] public APIs are documented (docstring + README if needed)
- [ ] no debug prints
- [ ] no secrets
- [ ] error handling and logging are appropriate
- [ ] British English used in comments and docstrings (agent requirement)
