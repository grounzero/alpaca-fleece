# MEMORY.md - Critical Operational Rules

## 1. NEVER Touch Code Directly — Always Spawn Agents

**Date Learned:** 2026-02-07
**Severity:** CRITICAL

**Rule:** I must NEVER edit source code files myself. Always spawn the appropriate specialist agent.

**Agent Mapping:**
| Task Type | Agent to Spawn |
|-----------|----------------|
| Python code | python-dev |
| Ruff linting | python-dev |
| Black formatting | python-dev |
| isort imports | python-dev |
| mypy type checking | python-dev |
| Testing | python-dev |
| Git operations (non-code) | main agent |

**Why:** Agents have specialised prompts, coding standards, and verification procedures that I don't have access to.

**Consequences of Violation:**
- Code may not follow project standards
- Tests may not pass
- Security issues may be introduced
- User trust is eroded

## 2. CI/CD Is Iterative

**Date Learned:** 2026-02-08

When CI fails on linting/formatting checks:
1. **Spawn the appropriate agent** to fix the specific issue
2. **Don't batch fixes** — one fix at a time, let CI validate
3. **Be patient** — CI may have different versions than local
4. **Each fix gets its own commit**

CI environments often differ from local. What passes locally may fail in CI. Fix iteratively.

## 3. Pre-commit Hooks Save Time

**Date Learned:** 2026-02-08

Set up pre-commit hooks to run checks BEFORE committing:
- Catches issues before CI
- Prevents broken commits
- Saves CI minutes

Use: `pre-commit install` after setting up `.pre-commit-config.yaml`

---

**Enforcement:** Absolute — no exceptions.
