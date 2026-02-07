#!/bin/bash
# Run bot from script location
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"
source .venv/bin/activate
exec python -u orchestrator.py >> logs/orchestrator.out 2>&1
