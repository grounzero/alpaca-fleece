"""Tests for runtime reconciler."""

import asyncio
import json
import sqlite3
from datetime import datetime, timezone

import pytest

from src.position_tracker import PositionTracker
from src.runtime_reconciler import RuntimeReconciler
from src.state_store import StateStore


@pytest.fixture
def position_tracker(mock_broker, state_store):
    """Position tracker with mock broker."""
    return PositionTracker(
        broker=mock_broker,
        state_store=state_store,
        trailing_stop_enabled=False,
    )


@pytest.fixture
def runtime_reconciler(mock_broker, state_store, position_tracker):
    """Runtime reconciler with mock broker and state store."""
    return RuntimeReconciler(
        broker=mock_broker,
        state_store=state_store,
        position_tracker=position_tracker,
        check_interval_seconds=1,  # Fast for testing
        repair_stuck_exits=True,
        halt_on_discrepancy=True,
    )


@pytest.mark.asyncio
async def test_start_stop(runtime_reconciler):
    """Test basic lifecycle: start and stop."""
    assert not runtime_reconciler._running

    await runtime_reconciler.start()
    assert runtime_reconciler._running
    assert runtime_reconciler._monitor_task is not None

    await asyncio.sleep(0.1)  # Let monitor loop run once

    await runtime_reconciler.stop()
    assert not runtime_reconciler._running


@pytest.mark.asyncio
async def test_monitor_loop_runs_periodically(runtime_reconciler, mock_broker):
    """Test that monitor loop runs periodically."""
    # Set up broker to return empty state
    mock_broker.get_open_orders.return_value = []
    mock_broker.get_positions.return_value = []

    call_count = 0
    original_method = runtime_reconciler._run_reconciliation_check

    async def counting_wrapper():
        nonlocal call_count
        call_count += 1
        return await original_method()

    runtime_reconciler._run_reconciliation_check = counting_wrapper

    await runtime_reconciler.start()
    await asyncio.sleep(1.5)  # Should run at least once with 1s interval
    await runtime_reconciler.stop()

    assert call_count >= 1


@pytest.mark.asyncio
async def test_reconciler_updates_intent_to_terminal_filled(
    runtime_reconciler, mock_broker, state_store
):
    """Test Rule 1: Alpaca has terminal order, SQLite has non-terminal → UPDATE."""
    # Insert a non-terminal order in SQLite
    state_store.save_order_intent(
        client_order_id="test-order-1",
        symbol="AAPL",
        side="buy",
        qty=10.0,
        status="accepted",
    )

    # Broker returns the order as filled
    mock_broker.get_open_orders.return_value = [
        {
            "id": "alpaca-123",
            "client_order_id": "test-order-1",
            "symbol": "AAPL",
            "side": "buy",
            "status": "filled",
            "filled_qty": 10.0,
        }
    ]
    mock_broker.get_positions.return_value = []

    # Run reconciliation
    await runtime_reconciler._run_reconciliation_check()

    # Verify order updated to filled
    orders = state_store.get_all_order_intents()
    assert len(orders) == 1
    assert orders[0]["status"] == "filled"
    assert orders[0]["filled_qty"] == 10.0
    assert orders[0]["alpaca_order_id"] == "alpaca-123"


@pytest.mark.asyncio
async def test_reconciler_marks_intent_failed_on_rejected(
    runtime_reconciler, mock_broker, state_store
):
    """Test Rule 1: Alpaca has rejected order → UPDATE SQLite to rejected."""
    # Insert a pending order in SQLite
    state_store.save_order_intent(
        client_order_id="test-order-2",
        symbol="MSFT",
        side="buy",
        qty=5.0,
        status="pending_new",
    )

    # Broker returns the order as rejected
    mock_broker.get_open_orders.return_value = [
        {
            "id": "alpaca-456",
            "client_order_id": "test-order-2",
            "symbol": "MSFT",
            "side": "buy",
            "status": "rejected",
            "filled_qty": 0.0,
        }
    ]
    mock_broker.get_positions.return_value = []

    # Run reconciliation
    await runtime_reconciler._run_reconciliation_check()

    # Verify order updated to rejected
    orders = state_store.get_all_order_intents()
    assert len(orders) == 1
    assert orders[0]["status"] == "rejected"


@pytest.mark.asyncio
async def test_reconciler_detects_broker_order_missing_intent(
    runtime_reconciler, mock_broker, state_store
):
    """Test Rule 3: Alpaca has order not in SQLite → DISCREPANCY."""
    # No orders in SQLite
    assert len(state_store.get_all_order_intents()) == 0

    # Broker returns an order
    mock_broker.get_open_orders.return_value = [
        {
            "id": "alpaca-999",
            "client_order_id": "mystery-order",
            "symbol": "TSLA",
            "side": "buy",
            "status": "accepted",
            "filled_qty": 0.0,
        }
    ]
    mock_broker.get_positions.return_value = []

    # Run reconciliation
    report = await runtime_reconciler._run_reconciliation_check()

    # Verify discrepancy detected
    assert report["status"] == "discrepancies_found"
    assert len(report["discrepancies"]) == 1
    assert report["discrepancies"][0]["type"] == "order_not_in_sqlite"
    assert report["discrepancies"][0]["client_order_id"] == "mystery-order"

    # Verify trading halted
    assert state_store.get_state("trading_halted") == "true"


@pytest.mark.asyncio
async def test_reconciler_detects_untracked_position(runtime_reconciler, mock_broker, state_store):
    """Test Rule 4: Alpaca has position not in SQLite → DISCREPANCY."""
    # No positions in snapshot
    # Broker returns a position
    mock_broker.get_open_orders.return_value = []
    mock_broker.get_positions.return_value = [
        {
            "symbol": "NVDA",
            "qty": 100,
            "avg_entry_price": 500.0,
        }
    ]

    # Run reconciliation
    report = await runtime_reconciler._run_reconciliation_check()

    # Verify discrepancy detected
    assert report["status"] == "discrepancies_found"
    assert len(report["discrepancies"]) == 1
    assert report["discrepancies"][0]["type"] == "position_not_in_sqlite"
    assert report["discrepancies"][0]["symbol"] == "NVDA"

    # Verify trading halted
    assert state_store.get_state("trading_halted") == "true"


@pytest.mark.asyncio
async def test_reconciler_repairs_stuck_pending_exit(
    runtime_reconciler, mock_broker, state_store, position_tracker
):
    """Test stuck pending_exit flag is cleared when no exit order exists."""
    # Create a position with pending_exit=True
    position = position_tracker.start_tracking(
        symbol="AAPL",
        fill_price=150.0,
        qty=10.0,
        side="long",
    )
    position.pending_exit = True
    position_tracker.upsert_position(position)

    # Verify pending_exit is set
    tracked_pos = position_tracker.get_position("AAPL")
    assert tracked_pos is not None
    assert tracked_pos.pending_exit is True

    # Broker shows position still open, no exit orders
    mock_broker.get_open_orders.return_value = []
    mock_broker.get_positions.return_value = [
        {
            "symbol": "AAPL",
            "qty": 10,
            "avg_entry_price": 150.0,
        }
    ]

    # Run reconciliation
    report = await runtime_reconciler._run_reconciliation_check()

    # Verify repair was applied
    assert len(report["repairs"]) == 1
    assert report["repairs"][0]["type"] == "stuck_pending_exit"
    assert report["repairs"][0]["symbol"] == "AAPL"

    # Verify pending_exit flag cleared
    tracked_pos = position_tracker.get_position("AAPL")
    assert tracked_pos is not None
    assert tracked_pos.pending_exit is False


@pytest.mark.asyncio
async def test_reconciler_blocks_on_api_outage(runtime_reconciler, mock_broker, state_store):
    """Test that reconciler handles broker API outage gracefully."""
    # Simulate broker timeout
    mock_broker.get_open_orders.side_effect = asyncio.TimeoutError("Broker timeout")

    # Run reconciliation
    report = await runtime_reconciler._run_reconciliation_check()

    # Verify status is broker_unavailable
    assert report["status"] == "broker_unavailable"
    assert (
        "unavailable" in report["error_message"].lower()
        or "timed out" in report["error_message"].lower()
    )

    # Verify broker health degraded
    assert state_store.get_state("broker_health") == "degraded"


@pytest.mark.asyncio
async def test_clean_reconciliation_passes(runtime_reconciler, mock_broker, state_store):
    """Test that clean reconciliation with no discrepancies passes."""
    # Empty state everywhere
    mock_broker.get_open_orders.return_value = []
    mock_broker.get_positions.return_value = []

    # Run reconciliation
    report = await runtime_reconciler._run_reconciliation_check()

    # Verify clean status
    assert report["status"] == "clean"
    assert len(report["discrepancies"]) == 0
    assert len(report["repairs"]) == 0

    # Verify trading not halted
    halt_state = state_store.get_state("trading_halted")
    assert halt_state != "true"


@pytest.mark.asyncio
async def test_does_not_repair_if_exit_order_working(
    runtime_reconciler, mock_broker, state_store, position_tracker
):
    """Test that pending_exit flag is NOT cleared if exit order is working."""
    # Create a long position with pending_exit=True
    position = position_tracker.start_tracking(
        symbol="MSFT",
        fill_price=300.0,
        qty=5.0,
        side="long",
    )
    position.pending_exit = True
    position_tracker.upsert_position(position)

    # Insert a working exit order in SQLite
    state_store.save_order_intent(
        client_order_id="exit-order-1",
        symbol="MSFT",
        side="sell",  # Opposite of long position
        qty=5.0,
        status="accepted",
    )

    # Broker shows position and exit order
    mock_broker.get_open_orders.return_value = [
        {
            "id": "alpaca-exit-1",
            "client_order_id": "exit-order-1",
            "symbol": "MSFT",
            "side": "sell",
            "status": "accepted",
            "filled_qty": 0.0,
        }
    ]
    mock_broker.get_positions.return_value = [
        {
            "symbol": "MSFT",
            "qty": 5,
            "avg_entry_price": 300.0,
        }
    ]

    # Run reconciliation
    report = await runtime_reconciler._run_reconciliation_check()

    # Verify NO repair (exit order is working)
    assert len(report["repairs"]) == 0

    # Verify pending_exit flag still set
    tracked_pos = position_tracker.get_position("MSFT")
    assert tracked_pos is not None
    assert tracked_pos.pending_exit is True


@pytest.mark.asyncio
async def test_does_not_repair_if_position_closed(
    runtime_reconciler, mock_broker, state_store, position_tracker
):
    """Test that pending_exit is repaired if position already closed in broker."""
    # Create a position with pending_exit=True
    position = position_tracker.start_tracking(
        symbol="TSLA",
        fill_price=200.0,
        qty=20.0,
        side="long",
    )
    position.pending_exit = True
    position_tracker.upsert_position(position)

    # Broker shows position CLOSED (not in positions list)
    mock_broker.get_open_orders.return_value = []
    mock_broker.get_positions.return_value = []  # Position closed

    # Run reconciliation
    report = await runtime_reconciler._run_reconciliation_check()

    # Verify repair was applied (position closed but flag still set)
    assert len(report["repairs"]) == 1
    assert report["repairs"][0]["symbol"] == "TSLA"

    # Verify pending_exit flag cleared
    tracked_pos = position_tracker.get_position("TSLA")
    assert tracked_pos is not None
    assert tracked_pos.pending_exit is False


@pytest.mark.asyncio
async def test_discrepancy_sets_trading_halted(runtime_reconciler, mock_broker, state_store):
    """Test that discrepancies set trading_halted flag."""
    # Start with clean state
    state_store.set_state("trading_halted", "false")

    # Create a discrepancy (untracked order)
    mock_broker.get_open_orders.return_value = [
        {
            "id": "alpaca-rogue",
            "client_order_id": "rogue-order",
            "symbol": "AMD",
            "side": "buy",
            "status": "accepted",
            "filled_qty": 0.0,
        }
    ]
    mock_broker.get_positions.return_value = []

    # Run reconciliation
    await runtime_reconciler._run_reconciliation_check()

    # Verify trading halted
    assert state_store.get_state("trading_halted") == "true"


@pytest.mark.asyncio
async def test_persists_reconciliation_report(runtime_reconciler, mock_broker, state_store):
    """Test that reconciliation reports are persisted to database."""
    # Clean state
    mock_broker.get_open_orders.return_value = []
    mock_broker.get_positions.return_value = []

    # Run reconciliation
    await runtime_reconciler._run_reconciliation_check()

    # Query reports table
    conn = sqlite3.connect(state_store.db_path)
    cursor = conn.cursor()
    cursor.execute("""
        SELECT timestamp_utc, check_type, status, discrepancies_count, repaired_count
        FROM reconciliation_reports
        ORDER BY timestamp_utc DESC
        LIMIT 1
    """)
    row = cursor.fetchone()
    conn.close()

    # Verify report persisted
    assert row is not None
    timestamp_utc, check_type, status, disc_count, repair_count = row
    assert check_type == "runtime"
    assert status == "clean"
    assert disc_count == 0
    assert repair_count == 0


@pytest.mark.asyncio
async def test_consecutive_failures_degrade_mode(runtime_reconciler, mock_broker, state_store):
    """Test that consecutive failures degrade to warning-only mode."""
    # Simulate repeated broker failures
    mock_broker.get_open_orders.side_effect = ConnectionError("Network error")

    # Run reconciliation 3+ times to hit max consecutive failures
    for i in range(4):
        await runtime_reconciler._run_reconciliation_check()

    # Verify consecutive failures tracked
    assert runtime_reconciler._consecutive_failures >= 3

    # Verify broker health degraded
    assert state_store.get_state("broker_health") == "degraded"


@pytest.mark.asyncio
async def test_auto_recovery_on_clean_check(runtime_reconciler, mock_broker, state_store):
    """Test that trading_halted is cleared on clean check after discrepancy."""
    # Start with trading halted
    state_store.set_state("trading_halted", "true")

    # Clean broker state
    mock_broker.get_open_orders.return_value = []
    mock_broker.get_positions.return_value = []

    # Run reconciliation
    await runtime_reconciler._run_reconciliation_check()

    # Verify trading halt cleared (auto-recovery)
    assert state_store.get_state("trading_halted") == "false"
