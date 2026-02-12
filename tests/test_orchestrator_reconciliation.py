"""Tests for orchestrator Phase 1 reconciliation auto-sync branches.

These tests monkeypatch `orchestrator` internals to simulate:
- initial `reconcile` failure followed by successful auto-sync+reconcile
- initial `reconcile` failure with sync process returning non-zero
- initial `reconcile` failure with sync timing out
"""

import subprocess
from unittest.mock import MagicMock

import pytest

import orchestrator as orch
from orchestrator import Orchestrator
from src.reconciliation import ReconciliationError


def _base_env():
    return {
        "ALPACA_API_KEY": "key",
        "ALPACA_SECRET_KEY": "secret",
        "ALPACA_PAPER": True,
        "CONFIG_PATH": "cfg",
        "DATABASE_PATH": ":memory:",
    }


def _base_trading_cfg():
    return {"strategy": {"name": "sma"}, "alerts": {}, "risk": {}, "trading": {}}


def _setup_basic_monkeypatch(monkeypatch):
    # Basic environment/config/validator
    monkeypatch.setattr(orch, "load_env", lambda: _base_env())
    monkeypatch.setattr(orch, "load_trading_config", lambda p: _base_trading_cfg())
    monkeypatch.setattr(orch, "validate_config", lambda e, c: None)

    # Broker mock with minimal account info
    broker = MagicMock()
    broker.get_account.return_value = {
        "equity": 1000.0,
        "buying_power": 2000.0,
        "cash": 500.0,
    }
    monkeypatch.setattr(orch, "Broker", lambda **kw: broker)

    # StateStore and AlertNotifier minimal mocks
    monkeypatch.setattr(orch, "StateStore", lambda db: MagicMock())
    monkeypatch.setattr(orch, "AlertNotifier", lambda **kw: MagicMock(enabled=False))


@pytest.mark.asyncio
async def test_phase1_auto_sync_success(monkeypatch):
    """When reconcile fails, auto-sync succeeds and a second reconcile passes."""
    _setup_basic_monkeypatch(monkeypatch)

    # reconcile: first call raises, second call succeeds
    call_count = {"n": 0}

    async def fake_reconcile(broker, state_store):
        call_count["n"] += 1
        if call_count["n"] == 1:
            raise ReconciliationError("initial mismatch")
        return None

    monkeypatch.setattr(orch, "reconcile", fake_reconcile)

    # subprocess.run should report success for both sync and snapshot scripts
    def fake_run(args, **kwargs):
        return subprocess.CompletedProcess(args=args, returncode=0, stdout="ok", stderr="")

    monkeypatch.setattr("subprocess.run", fake_run)

    orch_instance = Orchestrator()
    result = await orch_instance.phase1_infrastructure()

    assert result["reconciliation"]["status"] == "clean"


@pytest.mark.asyncio
async def test_phase1_auto_sync_sync_fails(monkeypatch):
    """When reconcile fails and sync process returns non-zero, reconciliation is reported as discrepancies."""
    _setup_basic_monkeypatch(monkeypatch)

    async def _raise_reconcile(b, s):
        raise ReconciliationError("mismatch")

    monkeypatch.setattr(orch, "reconcile", _raise_reconcile)

    def fake_run_fail(args, **kwargs):
        return subprocess.CompletedProcess(args=args, returncode=2, stdout="", stderr="failed")

    monkeypatch.setattr("subprocess.run", fake_run_fail)

    orch_instance = Orchestrator()
    result = await orch_instance.phase1_infrastructure()

    assert result["reconciliation"]["status"] == "discrepancies_found"
    assert any(
        "Position sync failed" in e or "Position sync failed" in str(e)
        for e in result.get("errors", [])
    )


@pytest.mark.asyncio
async def test_phase1_auto_sync_sync_timeout_raises(monkeypatch):
    """When the sync subprocess times out, phase1 should raise ReconciliationError."""
    _setup_basic_monkeypatch(monkeypatch)

    async def _raise_reconcile(b, s):
        raise ReconciliationError("mismatch")

    monkeypatch.setattr(orch, "reconcile", _raise_reconcile)

    def fake_run_timeout(args, **kwargs):
        raise subprocess.TimeoutExpired(cmd=args, timeout=60)

    monkeypatch.setattr("subprocess.run", fake_run_timeout)

    orch_instance = Orchestrator()
    with pytest.raises(ReconciliationError):
        await orch_instance.phase1_infrastructure()
