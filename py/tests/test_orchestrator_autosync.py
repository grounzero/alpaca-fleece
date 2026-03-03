import subprocess

import pytest


@pytest.mark.asyncio
async def test_phase1_autosync_success(monkeypatch, tmp_path):
    import orchestrator

    # Minimal env and config
    env = {
        "ALPACA_API_KEY": "key",
        "ALPACA_SECRET_KEY": "secret",
        "ALPACA_PAPER": True,
        "CONFIG_PATH": "config/trading.yaml",
        "DATABASE_PATH": str(tmp_path / "data" / "trades.db"),
    }

    trading_config = {"strategy": {"name": "sma"}, "symbols": {"equity_symbols": []}, "risk": {}}

    monkeypatch.setattr(orchestrator, "load_env", lambda: env)
    monkeypatch.setattr(orchestrator, "load_trading_config", lambda p: trading_config)
    monkeypatch.setattr(orchestrator, "validate_config", lambda e, c: None)

    class MockBroker:
        def __init__(self, api_key, secret_key, paper):
            pass

        def get_account(self):
            return {"equity": 1000.0, "buying_power": 1000.0, "cash": 1000.0}

    monkeypatch.setattr(orchestrator, "Broker", MockBroker)

    # Replace StateStore with a lightweight mock
    class MockStateStore:
        def __init__(self, path):
            pass

    monkeypatch.setattr(orchestrator, "StateStore", MockStateStore)

    # reconcile: fail first, succeed second
    calls = {"n": 0}

    async def reconcile_stub(broker, state_store):
        if calls["n"] == 0:
            calls["n"] += 1
            from src.reconciliation import ReconciliationError

            raise ReconciliationError("discrepancies")

    monkeypatch.setattr(orchestrator, "reconcile", reconcile_stub)

    # Patch subprocess.run to simulate successful sync and snapshot update
    def fake_run(args, capture_output, text, timeout, **kwargs):
        return subprocess.CompletedProcess(args=args, returncode=0, stdout="", stderr="")

    monkeypatch.setattr("subprocess.run", fake_run)

    orch = orchestrator.Orchestrator()
    result = await orch.phase1_infrastructure()

    assert result["reconciliation"]["status"] == "clean"


@pytest.mark.asyncio
async def test_phase1_autosync_failure(monkeypatch, tmp_path):
    import orchestrator

    env = {
        "ALPACA_API_KEY": "key",
        "ALPACA_SECRET_KEY": "secret",
        "ALPACA_PAPER": True,
        "CONFIG_PATH": "config/trading.yaml",
        "DATABASE_PATH": str(tmp_path / "data" / "trades.db"),
    }

    trading_config = {"strategy": {"name": "sma"}, "symbols": {"equity_symbols": []}, "risk": {}}

    monkeypatch.setattr(orchestrator, "load_env", lambda: env)
    monkeypatch.setattr(orchestrator, "load_trading_config", lambda p: trading_config)
    monkeypatch.setattr(orchestrator, "validate_config", lambda e, c: None)

    class MockBroker:
        def __init__(self, api_key, secret_key, paper):
            pass

        def get_account(self):
            return {"equity": 1000.0, "buying_power": 1000.0, "cash": 1000.0}

    monkeypatch.setattr(orchestrator, "Broker", MockBroker)

    class MockStateStore:
        def __init__(self, path):
            pass

    monkeypatch.setattr(orchestrator, "StateStore", MockStateStore)

    # reconcile always fails
    async def reconcile_always_fail(broker, state_store):
        from src.reconciliation import ReconciliationError

        raise ReconciliationError("discrepancies")

    monkeypatch.setattr(orchestrator, "reconcile", reconcile_always_fail)

    # subprocess.run returns non-zero to simulate sync failure
    def fake_run_fail(args, capture_output, text, timeout, **kwargs):
        return subprocess.CompletedProcess(args=args, returncode=1, stdout="", stderr="failed")

    monkeypatch.setattr("subprocess.run", fake_run_fail)

    orch = orchestrator.Orchestrator()
    result = await orch.phase1_infrastructure()

    assert result["reconciliation"]["status"] == "discrepancies_found"
    assert "errors" in result
