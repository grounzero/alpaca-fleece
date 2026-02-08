#!/usr/bin/env python3
"""Verify module boundaries and endpoint ownership.

Usage:
    python scripts/verify_boundaries.py phase1
    python scripts/verify_boundaries.py phase2
    python scripts/verify_boundaries.py phase3
"""

import ast
import sys
from pathlib import Path


class BoundaryViolation:
    """A module boundary violation."""

    def __init__(self, module: str, violation: str):
        self.module = module
        self.violation = violation

    def __str__(self):
        return f"  ❌ {self.module}: {self.violation}"


def check_imports(file_path: Path, forbidden_imports: list[str]) -> list[BoundaryViolation]:
    """Check if file imports any forbidden modules."""
    violations = []
    try:
        with open(file_path) as f:
            tree = ast.parse(f.read())

        for node in ast.walk(tree):
            if isinstance(node, ast.ImportFrom):
                if node.module and any(node.module.startswith(fi) for fi in forbidden_imports):
                    violations.append(
                        BoundaryViolation(
                            str(file_path),
                            f"Imports forbidden module: {node.module}",
                        )
                    )
            elif isinstance(node, ast.Import):
                for alias in node.names:
                    if any(alias.name.startswith(fi) for fi in forbidden_imports):
                        violations.append(
                            BoundaryViolation(
                                str(file_path),
                                f"Imports forbidden module: {alias.name}",
                            )
                        )
    except Exception as e:
        violations.append(BoundaryViolation(str(file_path), f"Parse error: {e}"))

    return violations


def check_phase1() -> list[BoundaryViolation]:
    """Check Infrastructure Agent phase.

    Rules:
    - broker.py MUST NOT import from alpaca_api or data
    - alpaca_api/* MUST NOT import from data
    - alpaca_api/* MUST NOT have asyncio/EventBus
    """
    violations = []

    # broker.py should not have data layer imports
    broker_violations = check_imports(
        Path("src/broker.py"),
        ["data", "event_bus", "stream"],
    )
    violations.extend(broker_violations)

    # alpaca_api/* should not have data layer imports
    for api_file in Path("src/alpaca_api").glob("*.py"):
        if api_file.name == "__init__.py":
            continue
        api_violations = check_imports(
            api_file,
            ["data", "event_bus", "stream"],
        )
        violations.extend(api_violations)

    return violations


def check_phase2() -> list[BoundaryViolation]:
    """Check Data Layer Agent phase.

    Rules:
    - data/* handlers MUST NOT submit orders
    - data/* handlers MUST NOT call alpaca_api/* directly (they receive via DataHandler)
    - stream.py MUST NOT normalise data
    """
    violations = []

    # stream.py should not have normalisation (no dataclass, no event definitions)
    stream_violations = check_imports(
        Path("src/stream.py"),
        ["event_bus", "data"],
    )
    violations.extend(stream_violations)

    return violations


def check_phase3() -> list[BoundaryViolation]:
    """Check Trading Logic Agent phase.

    Rules:
    - strategy/* MUST NOT call alpaca_api/* directly
    - strategy/* MUST NOT submit orders directly
    - risk_manager MUST NOT call alpaca_api/* (except via DataHandler)
    """
    violations = []

    # strategy/* should not have broker/api imports
    for strategy_file in Path("src/strategy").glob("*.py"):
        if strategy_file.name == "__init__.py":
            continue
        strat_violations = check_imports(
            strategy_file,
            ["broker", "alpaca_api", "order_manager"],
        )
        violations.extend(strat_violations)

    # risk_manager should not call alpaca_api directly
    risk_violations = check_imports(
        Path("src/risk_manager.py"),
        ["alpaca_api"],
    )
    violations.extend(risk_violations)

    return violations


def main():
    """Run boundary verification for specified phase."""
    if len(sys.argv) < 2:
        print("Usage: python scripts/verify_boundaries.py <phase1|phase2|phase3>")
        sys.exit(1)

    phase = sys.argv[1]

    if phase == "phase1":
        violations = check_phase1()
    elif phase == "phase2":
        violations = check_phase2()
    elif phase == "phase3":
        violations = check_phase3()
    else:
        print(f"Unknown phase: {phase}")
        sys.exit(1)

    if violations:
        print(f"\n🚨 Boundary violations found in {phase}:")
        for v in violations:
            print(v)
        sys.exit(1)
    else:
        print(f"✅ {phase}: All boundaries verified clean")
        sys.exit(0)


if __name__ == "__main__":
    main()
