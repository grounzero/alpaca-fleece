"""Tests for startup reconciliation."""

import pytest
from pathlib import Path

from src.reconciliation import reconcile, ReconciliationError


def test_reconciliation_detects_alpaca_order_not_in_sqlite(state_store, mock_broker):
    """Reconciliation detects orphaned orders in Alpaca."""
    # Alpaca has an open order not in SQLite
    mock_broker.get_open_orders.return_value = [
        {
            "id": "alpaca-order-123",
            "client_order_id": "unknown-id",
            "symbol": "AAPL",
            "status": "submitted",
            "filled_qty": 0,
        }
    ]
    mock_broker.get_positions.return_value = []

    # SQLite is empty
    # (no orders in state_store)

    # Reconciliation should fail
    with pytest.raises(ReconciliationError, match="discrepancies"):
        reconcile(mock_broker, state_store)

    # Should write error report
    assert Path("data/reconciliation_error.json").exists()


def test_reconciliation_detects_sqlite_terminal_alpaca_nonterminal(state_store, mock_broker):
    """Reconciliation detects order terminal locally but open in Alpaca."""
    client_id = "order-1"

    # SQLite has order as terminal (filled)
    state_store.save_order_intent(client_id, "AAPL", "buy", 10.0)
    state_store.update_order_intent(client_id, "filled", 10.0)

    # Alpaca has order as non-terminal (submitted)
    mock_broker.get_open_orders.return_value = [
        {
            "id": "alpaca-order-123",
            "client_order_id": client_id,
            "symbol": "AAPL",
            "status": "submitted",
            "filled_qty": 0,
        }
    ]
    mock_broker.get_positions.return_value = []

    # Reconciliation should fail
    with pytest.raises(ReconciliationError, match="discrepancies"):
        reconcile(mock_broker, state_store)


def test_reconciliation_clean_passes(state_store, mock_broker):
    """Reconciliation passes when state is clean."""
    # No orders in either system
    mock_broker.get_open_orders.return_value = []
    mock_broker.get_positions.return_value = []

    # Should not raise
    reconcile(mock_broker, state_store)


def test_reconciliation_updates_terminal_orders(state_store, mock_broker):
    """Reconciliation updates SQLite when Alpaca reports terminal."""
    client_id = "order-1"

    # SQLite has order as non-terminal (submitted)
    state_store.save_order_intent(client_id, "AAPL", "buy", 10.0)

    # Alpaca has order as terminal (filled)
    mock_broker.get_open_orders.return_value = [
        {
            "id": "alpaca-order-123",
            "client_order_id": client_id,
            "symbol": "AAPL",
            "status": "filled",
            "filled_qty": 10.0,
            "filled_avg_price": 100.5,
        }
    ]
    mock_broker.get_positions.return_value = []

    # Reconciliation should pass and update
    reconcile(mock_broker, state_store)

    # Order should now be marked as filled
    order = state_store.get_order_intent(client_id)
    assert order["status"] == "filled"
    assert order["filled_qty"] == 10.0
