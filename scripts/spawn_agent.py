#!/usr/bin/env python3
"""Helper to spawn agents with language standards pre-loaded."""

import sys
from pathlib import Path

# Path to language standards
LANGUAGE_STANDARDS_PATH = Path("/home/t-rox/.openclaw/agents/LANGUAGE_STANDARDS.md")


def load_standards() -> str:
    """Load language standards from shared file."""
    try:
        return LANGUAGE_STANDARDS_PATH.read_text()
    except FileNotFoundError:
        return """# Language Standards
## Rule: British English Only
- initialize → initialise
- behavior → behaviour
- color → colour (except API fields)
- center → centre
- organize → organise
- analyze → analyse
- optimize → optimise
- defense → defence
- license (noun) → licence

## Exceptions
API fields, library methods, protocols (HTTP, JSON) keep American spelling.
"""


def format_task_with_standards(task: str) -> str:
    """Prepend language standards to any agent task."""
    standards = load_standards()

    return f"""{standards}

---

## TASK INSTRUCTIONS

{task}

---

## MANDATORY VERIFICATION

Before completing this task, you MUST verify:
1. ✅ All comments use British English (not American)
2. ✅ All variable/function names use British English
3. ✅ No American spellings: initialize, behavior, color, center, organize, analyze, optimize
4. ✅ Exceptions ONLY for: API field names, library methods, protocols
5. ✅ Documentation uses British English throughout

If you find any American spellings, fix them before marking complete.
"""


def spawn_with_standards(task: str, agent_id: str = "main", **kwargs) -> dict:
    """Spawn an agent with language standards pre-loaded.

    Usage:
        from spawn_agent import spawn_with_standards

        result = spawn_with_standards(
            task="Refactor the order manager",
            agent_id="main",
            run_timeout_seconds=1800
        )

    Or from command line:
        python spawn_agent.py "Refactor order manager"
    """
    formatted_task = format_task_with_standards(task)

    # This would call the actual sessions_spawn
    # For now, just return the formatted task
    print("=" * 60)
    print("FORMATTED TASK WITH STANDARDS")
    print("=" * 60)
    print(formatted_task)
    print("=" * 60)

    return {"task": formatted_task, "agent_id": agent_id, "kwargs": kwargs}


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python spawn_agent.py 'Task description'")
        print("Example: python spawn_agent.py 'Refactor order manager'")
        sys.exit(1)

    task = sys.argv[1]
    result = spawn_with_standards(task)

    # Output can be piped to sessions_spawn or copied
    print("\nUse this formatted task when spawning agents.")
