#!/usr/bin/env python3
"""Static enforcement checks for AsyncBroker adapter rules.

This script fails if the sync `Broker` is referenced outside permitted locations
or if `asyncio.to_thread(self.broker` is used anywhere.
"""

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(__file__))

forbidden_patterns = [
    re.compile(r"asyncio\.to_thread\(self\.broker"),
    re.compile(r"from src\.broker import Broker"),
]

allowed_files_for_broker = {
    os.path.join(ROOT, "src", "broker.py"),
    os.path.join(ROOT, "src", "async_broker_adapter.py"),
    os.path.join(ROOT, "orchestrator.py"),
}

errors = []

for dirpath, dirnames, filenames in os.walk(ROOT):
    for fname in filenames:
        if not fname.endswith(".py"):
            continue
        path = os.path.join(dirpath, fname)
        # Skip archived code and test fixtures from this static check
        if "/Archive/" in path or "/tests/" in path:
            continue
        # Skip this checker script itself
        if path == os.path.join(ROOT, "tools", "check_async_broker.py"):
            continue
        try:
            with open(path, "r", encoding="utf-8") as f:
                src = f.read()
        except Exception:
            continue

        for pat in forbidden_patterns:
            for m in pat.finditer(src):
                # Skip occurrences inside commented lines
                line_start = src.rfind("\n", 0, m.start())
                if line_start == -1:
                    line_start = 0
                else:
                    line_start += 1
                line_end = src.find("\n", m.start())
                if line_end == -1:
                    line_end = len(src)
                line = src[line_start:line_end]
                if line.strip().startswith("#"):
                    continue

                # allow Broker import only in allowed files
                if pat.pattern == r"from src\.broker import Broker":
                    if path in allowed_files_for_broker:
                        continue

                errors.append(f"Forbidden pattern '{pat.pattern}' in {path}:{m.start()}")

if errors:
    for e in errors:
        print(e, file=sys.stderr)
    sys.exit(1)
else:
    print("Async broker static checks passed")
    sys.exit(0)
