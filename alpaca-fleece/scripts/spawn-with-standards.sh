#!/bin/bash
# Spawn an agent with language standards pre-loaded

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TASK="$1"

if [ -z "$TASK" ]; then
    echo "Usage: $0 'Task description'"
    echo "Example: $0 'Refactor order manager'"
    exit 1
fi

# Load standards and prepend to task
STANDARDS=$(cat /home/t-rox/.openclaw/agents/LANGUAGE_STANDARDS.md 2>/dev/null || echo "# British English Only: initialise, behaviour, colour, centre, organise, analyse, optimise")

FORMATTED_TASK="${STANDARDS}

---

## TASK INSTRUCTIONS

${TASK}

---

## MANDATORY VERIFICATION

Before completing this task, you MUST verify:
1. ✅ All comments use British English (not American)
2. ✅ All variable/function names use British English
3. ✅ No American spellings: initialize, behavior, color, center, organize, analyze, optimize
4. ✅ Exceptions ONLY for: API field names, library methods, protocols
5. ✅ Documentation uses British English throughout

If you find any American spellings, fix them before marking complete."

echo "========================================"
echo "FORMATTED TASK WITH STANDARDS"
echo "========================================"
echo "$FORMATTED_TASK"
echo "========================================"
echo ""
echo "Copy the above text and use with /sessions_spawn"
