Summary of changes in `feat/dynamic-stop`

Overview
--------
- Persist ATR values end-to-end (strategy -> order intent -> DB -> position tracker).
- Use ATR-based dynamic stops and profit targets in `ExitManager` when ATR is available.
- Added DB migration for the `atr` column and defensive parsing to ensure `atr` is `float | None`.
- Fixed several type/mypy issues and added unit tests for migration/persistence.
- Pinned reproducible dependency versions and added Dependabot config.

Why these changes
-----------------
- ATR persistence prevents loss of stop/target context across restarts and ensures exits use the same volatility reference used to size positions.
- Pinning dependencies (and adding `constraints.txt`) gives reproducible installs for CI and dev machines — important because `pandas-ta` pins native deps (`numba`, `llvmlite`).

What I changed (high level)
---------------------------
- `src/state_store.py`: persist and return `atr` as `float | None`; added migration to add `atr` for older DBs.
- `src/position_tracker.py`: include `atr` in persisted positions and normalize numeric types on load.
- `src/order_manager.py` / `orchestrator.py`: coerce `atr` read from DB to `float` when handling fills.
- `src/exit_manager.py`: explicit side validation for ATR-based stops, numeric validation of computed stops/targets.
- Tests: added migration/persistence tests and fixed typing/tests to pass under strict mypy.
- `pyproject.toml`: pinned tested dependency versions.
- `.github/dependabot.yml`: configured weekly pip update PRs.
- `constraints.txt`: added to repo root for reproducible pip installs.

Validation
----------
- Local CI: `pytest` (283 passed, 2 warnings), `mypy --strict` (success), `ruff` (no issues), `isort` (success).

Notes for reviewers
-------------------
- The repo currently uses `pandas-ta==0.4.71b0` which pins `numba==0.61.2` and `llvmlite<0.45`; I pinned these to ensure reproducible installs. If you prefer newer `numba/llvmlite`, we should evaluate replacing `pandas-ta` with an alternative.
- The `constraints.txt` is intentionally conservative; Dependabot will open PRs suggesting upgrades which can be reviewed and tested.

How to apply the `constraints.txt` locally
-----------------------------------------
Run:

```bash
python -m venv .venv
. .venv/bin/activate
pip install -U pip
pip install -r requirements.txt -c constraints.txt
```

Replace `requirements.txt` with your chosen install command (e.g., `pip install .[dev] -c constraints.txt`).

If you want, I can post this summary directly to the PR using the GitHub CLI (`gh`) — tell me to proceed and I will attempt it.
