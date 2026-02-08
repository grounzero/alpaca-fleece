# Memory: CI/CD Best Practices

**Date:** 2026-02-08

## Rule: Never Touch Code Directly

I must NEVER edit source code files myself. Always spawn the appropriate specialist agent.

## Rule: CI Failures Are Iterative

When CI fails on linting/formatting checks (ruff, black, isort, mypy):
1. **Spawn the appropriate agent** to fix the specific issue
2. **Don't try to batch fixes** — run one fix at a time and let CI validate
3. **Be patient** — CI can have version differences from local environment
4. **Each fix gets its own commit** for clear history

## Agent Mapping

| Task Type | Agent to Spawn |
|-----------|----------------|
| Python code | python-dev |
| Ruff linting | python-dev |
| Black formatting | python-dev |
| isort imports | python-dev |
| mypy type checking | python-dev |
| Testing | python-dev |
| Git operations | main agent (non-code) |

## Lesson Learned

CI environments often have slightly different versions or configurations than local. What passes locally may fail in CI. The solution is iterative fixing, not trying to get it perfect in one go.

**Enforcement:** Absolute — no exceptions.
