# Agent System Architecture

## Overview

The `agents/` folder contains **architectural contracts** describing the phase-based initialisation of the Alpaca trading bot. These are **documentation/specifications**, not isolated runtime workers.

## Implementation Approach

After evaluating the options:

1. **Isolated JSON Workers**: The original design in `infrastructure-worker.md` described isolated agents receiving JSON tasks and returning JSON outputs. This was deemed overly complex for a single-process Python trading bot.

2. **Phase-Based Orchestration**: The current implementation in `orchestrator.py` uses **direct method calls** organised into the same phases as the agent contracts. This provides:
   - Clean separation of concerns (same boundaries as agent contracts)
   - Type safety and IDE support (Python objects, not JSON)
   - Simpler debugging and testing (no IPC or serialisation)
   - Better performance (no message passing overhead)

## Agent Contracts

Each markdown file serves as a contract documenting:
- **Responsibilities**: What this phase owns
- **Constraints**: What it can/cannot do
- **Outputs**: Expected results

| File | Phase | Implementation |
|------|-------|----------------|
| `infrastructure.md` | Phase 1 | `orchestrator.py::phase1_infrastructure()` |
| `data_layer.md` | Phase 2 | `orchestrator.py::phase2_data_layer()` |
| `trading.md` | Phase 3 | `orchestrator.py::phase3_trading_logic()` |
| `test_qa.md` | Testing | `tests/` directory |
| `infrastructure-worker.md` | JSON spec | Output schema for Phase 1 |

## Usage

The orchestrator is the main entry point:

```bash
# Via main.py (backward compatible)
python main.py

# Direct
python orchestrator.py
```

## Future: Isolated Workers

If you need true agent isolation in the future (e.g., for microservices or sandboxed execution), you could:

1. Wrap each phase method in a JSON-RPC interface
2. Run agents in separate processes/containers
3. Use the output schemas in `infrastructure-worker.md` as the API contract

The phase boundaries are already designed to support this migration if needed.
