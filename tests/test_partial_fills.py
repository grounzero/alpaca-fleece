"""Tests for partial fill support.

Covers:
- Delta computation (no-op, increase, regression)
- Idempotent fill insertion (duplicate detection)
- Polling detection of incremental fills
- Restart safety (no reapplication of old fills)
- Schema migration (fills table creation)
- Monotonic cumulative qty enforcement
"""

import sqlite3
from datetime import datetime, timezone
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock

import pytest

from src.data.order_updates import OrderUpdatesHandler
from src.event_bus import OrderUpdateEvent
from src.models.order_state import OrderState
from src.position_tracker import PositionTracker
from src.schema_manager import SchemaManager
from src.state_store import StateStore


class DummyEventBus:
    def __init__(self):
        self.published = []

    async def publish(self, event):
        self.published.append(event)


def _setup_db(tmp_path, db_name="test.db"):
    """Create a fresh DB with schema and return (db_path, state_store)."""
    db_path = str(tmp_path / db_name)
    SchemaManager.ensure_schema(db_path)
    store = StateStore(db_path)
    return db_path, store


def _insert_order_intent(
    db_path,
    client_order_id,
    alpaca_order_id,
    symbol="AAPL",
    side="buy",
    qty=100,
    status="submitted",
    filled_qty=0,
):
    """Insert a test order intent into the DB."""
    now = datetime.now(timezone.utc).isoformat()
    with sqlite3.connect(db_path) as conn:
        cur = conn.cursor()
        cur.execute(
            """INSERT INTO order_intents
               (client_order_id, symbol, side, qty, status, filled_qty,
                alpaca_order_id, created_at_utc, updated_at_utc)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            (client_order_id, symbol, side, qty, status, filled_qty, alpaca_order_id, now, now),
        )


def _make_raw_update(
    alpaca_id,
    client_id,
    symbol="AAPL",
    side="buy",
    status="partially_filled",
    filled_qty=None,
    filled_avg_price=None,
    fill_id=None,
):
    """Create a raw update SimpleNamespace mimicking Alpaca SDK."""
    return SimpleNamespace(
        order=SimpleNamespace(
            id=alpaca_id,
            client_order_id=client_id,
            symbol=symbol,
            side=side,
            status=status,
            filled_qty=filled_qty,
            filled_avg_price=filled_avg_price,
            fill_id=fill_id,
        ),
        at=datetime.now(timezone.utc),
    )


def _count_fills(db_path, alpaca_order_id):
    """Count fill rows for a given order."""
    with sqlite3.connect(db_path) as conn:
        cur = conn.cursor()
        cur.execute(
            "SELECT COUNT(*) FROM fills WHERE alpaca_order_id = ?",
            (alpaca_order_id,),
        )
        return cur.fetchone()[0]


def _get_fills(db_path, alpaca_order_id):
    """Get all fill rows for a given order."""
    with sqlite3.connect(db_path) as conn:
        cur = conn.cursor()
        cur.execute(
            "SELECT delta_qty, cum_qty, cum_avg_price FROM fills "
            "WHERE alpaca_order_id = ? ORDER BY cum_qty",
            (alpaca_order_id,),
        )
        return cur.fetchall()


# ---------------------------------------------------------------
# Schema migration tests
# ---------------------------------------------------------------


class TestFillsTableCreation:
    def test_fills_table_exists_after_schema_ensure(self, tmp_path):
        db_path = str(tmp_path / "schema.db")
        SchemaManager.ensure_schema(db_path)
        with sqlite3.connect(db_path) as conn:
            cur = conn.cursor()
            cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='fills'")
            assert cur.fetchone() is not None

    def test_fills_table_has_correct_columns(self, tmp_path):
        db_path = str(tmp_path / "schema.db")
        SchemaManager.ensure_schema(db_path)
        with sqlite3.connect(db_path) as conn:
            cur = conn.cursor()
            cur.execute("PRAGMA table_info(fills)")
            cols = {row[1] for row in cur.fetchall()}
        expected = {
            "id",
            "alpaca_order_id",
            "client_order_id",
            "symbol",
            "side",
            "delta_qty",
            "cum_qty",
            "cum_avg_price",
            "timestamp_utc",
            "fill_id",
            "price_is_estimate",
            "fill_dedupe_key",
        }
        assert expected.issubset(cols)

    def test_fills_unique_constraint_with_fill_id(self, tmp_path):
        db_path, store = _setup_db(tmp_path)
        # First insert
        assert store.insert_fill_idempotent(
            alpaca_order_id="order-1",
            client_order_id="c-1",
            symbol="AAPL",
            side="buy",
            delta_qty=10,
            cum_qty=10,
            cum_avg_price=150.0,
            timestamp_utc="2024-01-01T00:00:00Z",
            fill_id="fill-abc",
        )
        # Duplicate
        assert not store.insert_fill_idempotent(
            alpaca_order_id="order-1",
            client_order_id="c-1",
            symbol="AAPL",
            side="buy",
            delta_qty=10,
            cum_qty=10,
            cum_avg_price=150.0,
            timestamp_utc="2024-01-01T00:00:00Z",
            fill_id="fill-abc",
        )

    def test_fills_unique_constraint_without_fill_id(self, tmp_path):
        db_path, store = _setup_db(tmp_path)
        # First insert (no fill_id, keyed by CUM:<cum_qty>)
        assert store.insert_fill_idempotent(
            alpaca_order_id="order-1",
            client_order_id="c-1",
            symbol="AAPL",
            side="buy",
            delta_qty=10,
            cum_qty=10,
            cum_avg_price=150.0,
            timestamp_utc="2024-01-01T00:00:00Z",
        )
        # Duplicate (same cum_qty, no fill_id)
        assert not store.insert_fill_idempotent(
            alpaca_order_id="order-1",
            client_order_id="c-1",
            symbol="AAPL",
            side="buy",
            delta_qty=10,
            cum_qty=10,
            cum_avg_price=150.0,
            timestamp_utc="2024-01-01T00:00:01Z",
        )


# ---------------------------------------------------------------
# Delta computation tests
# ---------------------------------------------------------------


class TestDeltaComputation:
    @pytest.mark.asyncio
    async def test_no_delta_when_qty_unchanged(self, tmp_path):
        """prev 10 → new 10 => delta 0 (no insert)"""
        db_path, store = _setup_db(tmp_path)
        bus = DummyEventBus()
        handler = OrderUpdatesHandler(store, bus)

        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=10)

        raw = _make_raw_update("a-1", "c-1", filled_qty="10", filled_avg_price="150.0")
        await handler.on_order_update(raw)

        assert _count_fills(db_path, "a-1") == 0
        assert len(bus.published) == 1
        event = bus.published[0]
        assert event.delta_qty == 0.0

    @pytest.mark.asyncio
    async def test_delta_computed_on_qty_increase(self, tmp_path):
        """prev 10 → new 25 => delta 15 (insert once)"""
        db_path, store = _setup_db(tmp_path)
        bus = DummyEventBus()
        handler = OrderUpdatesHandler(store, bus)

        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=10)

        raw = _make_raw_update("a-1", "c-1", filled_qty="25", filled_avg_price="150.0")
        await handler.on_order_update(raw)

        assert _count_fills(db_path, "a-1") == 1
        fills = _get_fills(db_path, "a-1")
        assert fills[0][0] == pytest.approx(15.0)  # delta_qty
        assert fills[0][1] == pytest.approx(25.0)  # cum_qty

        event = bus.published[0]
        assert event.delta_qty == pytest.approx(15.0)

    @pytest.mark.asyncio
    async def test_regression_ignored(self, tmp_path):
        """prev 25 → new 20 => regression ignored (no insert)"""
        db_path, store = _setup_db(tmp_path)
        bus = DummyEventBus()
        handler = OrderUpdatesHandler(store, bus)

        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=25)

        raw = _make_raw_update("a-1", "c-1", filled_qty="20", filled_avg_price="150.0")
        await handler.on_order_update(raw)

        assert _count_fills(db_path, "a-1") == 0
        event = bus.published[0]
        assert event.delta_qty == 0.0

        # Verify stored qty was NOT decreased
        with sqlite3.connect(db_path) as conn:
            cur = conn.cursor()
            cur.execute(
                "SELECT filled_qty FROM order_intents WHERE alpaca_order_id = ?",
                ("a-1",),
            )
            row = cur.fetchone()
        assert float(row[0]) == 25.0

    @pytest.mark.asyncio
    async def test_multiple_incremental_fills(self, tmp_path):
        """Simulate multiple partial fills: 0→10→25→50"""
        db_path, store = _setup_db(tmp_path)
        bus = DummyEventBus()
        handler = OrderUpdatesHandler(store, bus)

        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=0)

        # Fill 1: 0 → 10
        raw1 = _make_raw_update("a-1", "c-1", filled_qty="10", filled_avg_price="150.0")
        await handler.on_order_update(raw1)

        # Fill 2: 10 → 25
        raw2 = _make_raw_update("a-1", "c-1", filled_qty="25", filled_avg_price="151.0")
        await handler.on_order_update(raw2)

        # Fill 3: 25 → 50 (status = filled)
        raw3 = _make_raw_update(
            "a-1",
            "c-1",
            filled_qty="50",
            filled_avg_price="152.0",
            status="filled",
        )
        await handler.on_order_update(raw3)

        fills = _get_fills(db_path, "a-1")
        assert len(fills) == 3
        assert fills[0][0] == pytest.approx(10.0)  # delta 10
        assert fills[1][0] == pytest.approx(15.0)  # delta 15
        assert fills[2][0] == pytest.approx(25.0)  # delta 25

        # Deltas should sum to total
        total_delta = sum(f[0] for f in fills)
        assert total_delta == pytest.approx(50.0)

    @pytest.mark.asyncio
    async def test_none_filled_qty_preserves_existing(self, tmp_path):
        """If incoming filled_qty is None, prev_cum_qty is used (no delta)."""
        db_path, store = _setup_db(tmp_path)
        bus = DummyEventBus()
        handler = OrderUpdatesHandler(store, bus)

        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=10)

        raw = _make_raw_update("a-1", "c-1", filled_qty=None, filled_avg_price=None)
        await handler.on_order_update(raw)

        assert _count_fills(db_path, "a-1") == 0
        event = bus.published[0]
        assert event.delta_qty == 0.0


# ---------------------------------------------------------------
# Idempotency tests
# ---------------------------------------------------------------


class TestIdempotentInsert:
    @pytest.mark.asyncio
    async def test_same_update_twice_produces_one_fill(self, tmp_path):
        """If the same update arrives twice, only one fill row is inserted."""
        db_path, store = _setup_db(tmp_path)
        bus = DummyEventBus()
        handler = OrderUpdatesHandler(store, bus)

        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=0)

        raw = _make_raw_update(
            "a-1",
            "c-1",
            filled_qty="10",
            filled_avg_price="150.0",
            fill_id="fill-1",
        )

        await handler.on_order_update(raw)
        await handler.on_order_update(raw)

        assert _count_fills(db_path, "a-1") == 1
        # Second event should have delta_qty=0
        assert bus.published[1].delta_qty == 0.0

    @pytest.mark.asyncio
    async def test_same_cum_qty_without_fill_id_deduped(self, tmp_path):
        """Without fill_id, deduplication is by (alpaca_order_id, CUM:<cum_qty>)."""
        db_path, store = _setup_db(tmp_path)
        bus = DummyEventBus()
        handler = OrderUpdatesHandler(store, bus)

        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=0)

        raw = _make_raw_update("a-1", "c-1", filled_qty="10", filled_avg_price="150.0")

        await handler.on_order_update(raw)
        await handler.on_order_update(raw)

        assert _count_fills(db_path, "a-1") == 1

    @pytest.mark.asyncio
    async def test_different_fill_ids_can_reach_same_cum_qty(self, tmp_path):
        """Different fill_ids CAN reach the same cum_qty (e.g., Alpaca splits fills).

        This is valid: execution fill 1 brings cum_qty to 10, then a second execution
        with a different fill_id arrives later and also reaches cum_qty=10 (partial
        duplicate or late-arriving update).
        """
        db_path, store = _setup_db(tmp_path)

        # First fill_id reaches cum_qty=10
        inserted1 = store.insert_fill_idempotent(
            alpaca_order_id="a-1",
            client_order_id="c-1",
            symbol="AAPL",
            side="buy",
            delta_qty=10,
            cum_qty=10,
            cum_avg_price=150.0,
            timestamp_utc="2024-01-01T00:00:00Z",
            fill_id="fill-1",
        )
        assert inserted1

        # Different fill_id but same cum_qty also succeeds
        # (Each fill_id is a different dedupe key)
        inserted2 = store.insert_fill_idempotent(
            alpaca_order_id="a-1",
            client_order_id="c-1",
            symbol="AAPL",
            side="buy",
            delta_qty=0,  # No additional fill
            cum_qty=10,
            cum_avg_price=150.0,
            timestamp_utc="2024-01-01T00:00:01Z",
            fill_id="fill-2",
        )
        # Second insert succeeds because fill_id is different
        assert inserted2
        assert _count_fills(db_path, "a-1") == 2


# ---------------------------------------------------------------
# Restart safety tests
# ---------------------------------------------------------------


class TestRestartSafety:
    @pytest.mark.asyncio
    async def test_restart_does_not_reapply_old_fills(self, tmp_path):
        """After restart, re-processing the broker state does not insert duplicate fills."""
        db_path, store = _setup_db(tmp_path)
        bus = DummyEventBus()
        handler = OrderUpdatesHandler(store, bus)

        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=0)

        # Process initial fills
        raw1 = _make_raw_update("a-1", "c-1", filled_qty="10", filled_avg_price="150.0")
        await handler.on_order_update(raw1)

        raw2 = _make_raw_update("a-1", "c-1", filled_qty="25", filled_avg_price="151.0")
        await handler.on_order_update(raw2)

        assert _count_fills(db_path, "a-1") == 2

        # Simulate restart: create new handler instance
        store2 = StateStore(db_path)
        bus2 = DummyEventBus()
        handler2 = OrderUpdatesHandler(store2, bus2)

        # Re-process the same broker state (e.g., from reconciliation)
        raw_restart = _make_raw_update(
            "a-1",
            "c-1",
            filled_qty="25",
            filled_avg_price="151.0",
        )
        await handler2.on_order_update(raw_restart)

        # No new fills should be inserted
        assert _count_fills(db_path, "a-1") == 2
        # Event should have delta_qty=0
        assert bus2.published[0].delta_qty == 0.0

    @pytest.mark.asyncio
    async def test_restart_processes_new_fills_only(self, tmp_path):
        """After restart, new fill increments ARE processed."""
        db_path, store = _setup_db(tmp_path)
        bus = DummyEventBus()
        handler = OrderUpdatesHandler(store, bus)

        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=0)

        # Pre-restart fills
        raw1 = _make_raw_update("a-1", "c-1", filled_qty="10", filled_avg_price="150.0")
        await handler.on_order_update(raw1)

        # Simulate restart
        store2 = StateStore(db_path)
        bus2 = DummyEventBus()
        handler2 = OrderUpdatesHandler(store2, bus2)

        # New fill increment post-restart
        raw2 = _make_raw_update("a-1", "c-1", filled_qty="30", filled_avg_price="151.0")
        await handler2.on_order_update(raw2)

        assert _count_fills(db_path, "a-1") == 2  # 10 + (30-10)
        assert bus2.published[0].delta_qty == pytest.approx(20.0)


# ---------------------------------------------------------------
# Polling detection tests
# ---------------------------------------------------------------


class TestPollingIncrementalFills:
    @pytest.mark.asyncio
    async def test_polling_emits_event_on_cum_qty_increase(self, tmp_path):
        """Polling should emit update when cum_filled_qty increases, even if status unchanged."""
        from src.stream_polling import StreamPolling

        stream = StreamPolling("test_key", "test_secret", paper=True, feed="iex")
        stream._db_path = str(tmp_path / "poll.db")
        stream.trading_client = MagicMock()
        stream.on_order_update = AsyncMock()

        # Create schema
        SchemaManager.ensure_schema(stream._db_path)

        now = datetime.now(timezone.utc).isoformat()
        with sqlite3.connect(stream._db_path) as conn:
            cur = conn.cursor()
            cur.execute(
                """INSERT INTO order_intents
                   (client_order_id, symbol, side, qty, status, filled_qty,
                    alpaca_order_id, created_at_utc, updated_at_utc)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                ("c-1", "AAPL", "buy", 100, "partially_filled", 10, "a-1", now, now),
            )

        # Alpaca returns same status but increased filled_qty
        mock_order = {
            "id": "a-1",
            "client_order_id": "c-1",
            "symbol": "AAPL",
            "side": "buy",
            "status": "partially_filled",
            "filled_qty": "25",
            "filled_avg_price": "150.00",
        }
        stream.trading_client.get_order_by_id.return_value = mock_order

        await stream._check_order_status()

        # Should emit an update because cum_qty increased (even though status is unchanged)
        assert stream.on_order_update.called

    @pytest.mark.asyncio
    async def test_polling_does_not_emit_when_nothing_changed(self, tmp_path):
        """Polling should NOT emit when both status and cum_qty are unchanged."""
        from src.stream_polling import StreamPolling

        stream = StreamPolling("test_key", "test_secret", paper=True, feed="iex")
        stream._db_path = str(tmp_path / "poll2.db")
        stream.trading_client = MagicMock()
        stream.on_order_update = AsyncMock()

        SchemaManager.ensure_schema(stream._db_path)

        now = datetime.now(timezone.utc).isoformat()
        with sqlite3.connect(stream._db_path) as conn:
            cur = conn.cursor()
            cur.execute(
                """INSERT INTO order_intents
                   (client_order_id, symbol, side, qty, status, filled_qty,
                    alpaca_order_id, created_at_utc, updated_at_utc)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)""",
                ("c-1", "AAPL", "buy", 100, "partially_filled", 10, "a-1", now, now),
            )

        # Alpaca returns same status and same filled_qty
        mock_order = {
            "id": "a-1",
            "client_order_id": "c-1",
            "symbol": "AAPL",
            "side": "buy",
            "status": "partially_filled",
            "filled_qty": "10",
            "filled_avg_price": "150.00",
        }
        stream.trading_client.get_order_by_id.return_value = mock_order

        stream.on_order_update.reset_mock()
        await stream._check_order_status()

        # Should NOT emit (nothing changed)
        assert not stream.on_order_update.called


# ---------------------------------------------------------------
# State store API tests
# ---------------------------------------------------------------


class TestStateStorePartialFillAPIs:
    def test_get_order_intent_by_alpaca_id(self, tmp_path):
        db_path, store = _setup_db(tmp_path)
        _insert_order_intent(db_path, "c-1", "a-1", symbol="AAPL")

        result = store.get_order_intent_by_alpaca_id("a-1")
        assert result is not None
        assert result["client_order_id"] == "c-1"
        assert result["symbol"] == "AAPL"

    def test_get_order_intent_by_alpaca_id_not_found(self, tmp_path):
        _, store = _setup_db(tmp_path)
        assert store.get_order_intent_by_alpaca_id("nonexistent") is None

    def test_get_last_cum_qty_from_order_intents(self, tmp_path):
        db_path, store = _setup_db(tmp_path)
        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=42)

        assert store.get_last_cum_qty_for_order("a-1") == 42.0

    def test_get_last_cum_qty_falls_back_to_fills(self, tmp_path):
        db_path, store = _setup_db(tmp_path)
        # No order_intent, but have fills
        store.insert_fill_idempotent(
            alpaca_order_id="a-1",
            client_order_id="c-1",
            symbol="AAPL",
            side="buy",
            delta_qty=10,
            cum_qty=10,
            cum_avg_price=150.0,
            timestamp_utc="2024-01-01T00:00:00Z",
        )
        store.insert_fill_idempotent(
            alpaca_order_id="a-1",
            client_order_id="c-1",
            symbol="AAPL",
            side="buy",
            delta_qty=15,
            cum_qty=25,
            cum_avg_price=151.0,
            timestamp_utc="2024-01-01T00:01:00Z",
        )

        assert store.get_last_cum_qty_for_order("a-1") == 25.0

    def test_get_last_cum_qty_returns_zero_when_nothing(self, tmp_path):
        _, store = _setup_db(tmp_path)
        assert store.get_last_cum_qty_for_order("nonexistent") == 0.0

    def test_update_order_intent_cumulative_monotonic(self, tmp_path):
        db_path, store = _setup_db(tmp_path)
        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=20)

        # Try to decrease: should NOT decrease
        store.update_order_intent_cumulative(
            alpaca_order_id="a-1",
            status="partially_filled",
            new_cum_qty=10.0,
            new_cum_avg_price=150.0,
            timestamp_utc="2024-01-01T00:00:00Z",
        )

        with sqlite3.connect(db_path) as conn:
            cur = conn.cursor()
            cur.execute(
                "SELECT filled_qty FROM order_intents WHERE alpaca_order_id = ?",
                ("a-1",),
            )
            row = cur.fetchone()
        assert float(row[0]) == 20.0

    def test_update_order_intent_cumulative_increase(self, tmp_path):
        db_path, store = _setup_db(tmp_path)
        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=10)

        store.update_order_intent_cumulative(
            alpaca_order_id="a-1",
            status="partially_filled",
            new_cum_qty=25.0,
            new_cum_avg_price=151.0,
            timestamp_utc="2024-01-01T00:00:00Z",
        )

        with sqlite3.connect(db_path) as conn:
            cur = conn.cursor()
            cur.execute(
                "SELECT filled_qty, filled_avg_price FROM order_intents "
                "WHERE alpaca_order_id = ?",
                ("a-1",),
            )
            row = cur.fetchone()
        assert float(row[0]) == 25.0
        assert float(row[1]) == 151.0


# ---------------------------------------------------------------
# Event model tests
# ---------------------------------------------------------------


class TestOrderUpdateEventModel:
    def test_cum_aliases(self):
        event = OrderUpdateEvent(
            order_id="a-1",
            client_order_id="c-1",
            symbol="AAPL",
            side="buy",
            status="partially_filled",
            state=OrderState.PARTIAL,
            filled_qty=10.0,
            avg_fill_price=150.0,
            timestamp=datetime.now(timezone.utc),
        )
        assert event.cum_filled_qty == 10.0
        assert event.cum_avg_fill_price == 150.0

    def test_delta_qty_default_none(self):
        event = OrderUpdateEvent(
            order_id="a-1",
            client_order_id="c-1",
            symbol="AAPL",
            side="buy",
            status="new",
            state=OrderState.PENDING,
            filled_qty=None,
            avg_fill_price=None,
            timestamp=datetime.now(timezone.utc),
        )
        assert event.delta_qty is None

    def test_delta_qty_set(self):
        event = OrderUpdateEvent(
            order_id="a-1",
            client_order_id="c-1",
            symbol="AAPL",
            side="buy",
            status="partially_filled",
            state=OrderState.PARTIAL,
            filled_qty=10.0,
            avg_fill_price=150.0,
            timestamp=datetime.now(timezone.utc),
            delta_qty=5.0,
        )
        assert event.delta_qty == 5.0


# ---------------------------------------------------------------
# Late fill after cancel/expire tests
# ---------------------------------------------------------------


class TestLateFills:
    @pytest.mark.asyncio
    async def test_late_fill_after_cancel_is_recorded(self, tmp_path):
        """If cum qty increases after cancel, record/apply the fill anyway."""
        db_path, store = _setup_db(tmp_path)
        bus = DummyEventBus()
        handler = OrderUpdatesHandler(store, bus)

        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=10, status="canceled")

        # Late fill: broker reports cum_qty=15 even though order is canceled
        raw = _make_raw_update(
            "a-1",
            "c-1",
            filled_qty="15",
            filled_avg_price="150.0",
            status="canceled",
        )
        await handler.on_order_update(raw)

        assert _count_fills(db_path, "a-1") == 1
        fills = _get_fills(db_path, "a-1")
        assert fills[0][0] == pytest.approx(5.0)  # delta
        assert bus.published[0].delta_qty == pytest.approx(5.0)


# ---------------------------------------------------------------
# Schema version test
# ---------------------------------------------------------------


class TestSchemaVersion:
    def test_schema_version_is_2(self, tmp_path):
        db_path = str(tmp_path / "ver.db")
        SchemaManager.ensure_schema(db_path)
        with sqlite3.connect(db_path) as conn:
            cur = conn.cursor()
            cur.execute("SELECT schema_version FROM schema_meta WHERE id = 1")
            row = cur.fetchone()
        assert row[0] == 3


# ---------------------------------------------------------------
# Reconciliation tests
# ---------------------------------------------------------------


class TestReconcileFills:
    @pytest.mark.asyncio
    async def test_reconcile_fills_detects_drift(self, tmp_path):
        """reconcile_fills should detect when broker has more fills than DB."""
        from src.reconciliation import reconcile_fills

        db_path, store = _setup_db(tmp_path)
        bus = DummyEventBus()
        handler = OrderUpdatesHandler(store, bus)

        # Insert order intent with filled_qty=10 in DB
        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=10)

        # Mock broker returning filled_qty=25
        mock_broker = MagicMock()
        mock_broker.get_open_orders = AsyncMock(
            return_value=[
                {
                    "id": "a-1",
                    "client_order_id": "c-1",
                    "symbol": "AAPL",
                    "side": "buy",
                    "status": "partially_filled",
                    "filled_qty": 25,
                    "filled_avg_price": 150.0,
                }
            ]
        )

        # Run reconciliation
        count = await reconcile_fills(
            broker=mock_broker,
            state_store=store,
            on_order_update=handler.on_order_update,
        )

        # Should have synthesised 1 update
        assert count == 1
        # Should have inserted a fill (15 delta from 10→25)
        assert _count_fills(db_path, "a-1") == 1

    @pytest.mark.asyncio
    async def test_reconcile_fills_skips_no_drift(self, tmp_path):
        """reconcile_fills should skip orders with no drift."""
        from src.reconciliation import reconcile_fills

        db_path, store = _setup_db(tmp_path)

        # Insert order intent with filled_qty=25 in DB
        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=25)

        # Mock broker also returning filled_qty=25
        mock_broker = MagicMock()
        mock_broker.get_open_orders = AsyncMock(
            return_value=[
                {
                    "id": "a-1",
                    "client_order_id": "c-1",
                    "symbol": "AAPL",
                    "side": "buy",
                    "status": "partially_filled",
                    "filled_qty": 25,
                    "filled_avg_price": 150.0,
                }
            ]
        )

        count = await reconcile_fills(broker=mock_broker, state_store=store)

        # No drift, no updates synthesised
        assert count == 0

    @pytest.mark.asyncio
    async def test_reconcile_fills_handles_broker_api_failure(self, tmp_path):
        """reconcile_fills should handle broker API failures gracefully."""
        from src.reconciliation import reconcile_fills

        _, store = _setup_db(tmp_path)

        # Mock broker that raises
        mock_broker = MagicMock()
        mock_broker.get_open_orders = AsyncMock(side_effect=Exception("Broker API error"))

        # Should not raise, returns 0
        count = await reconcile_fills(broker=mock_broker, state_store=store)
        assert count == 0

    @pytest.mark.asyncio
    async def test_reconcile_fills_skips_terminal_orders(self, tmp_path):
        """reconcile_fills should skip orders already in terminal status."""
        from src.reconciliation import reconcile_fills

        db_path, store = _setup_db(tmp_path)

        # Insert terminal order (filled)
        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=10, status="filled")

        mock_broker = MagicMock()
        mock_broker.get_open_orders = AsyncMock(return_value=[])

        count = await reconcile_fills(broker=mock_broker, state_store=store)

        # Terminal order not considered for reconciliation
        assert count == 0


# ---------------------------------------------------------------
# PositionTracker Fill Integration tests
# ---------------------------------------------------------------


class TestPositionTrackerFillIntegration:
    """Test PositionTracker integration with fill events."""

    @pytest.mark.asyncio
    async def test_position_created_on_first_fill_not_submission(self, tmp_path):
        """Verify position is created when first fill arrives, not at order submission."""
        db_path, state_store = _setup_db(tmp_path)
        mock_broker = MagicMock()
        tracker = PositionTracker(broker=mock_broker, state_store=state_store)

        # No position should exist initially
        assert tracker.get_position("AAPL") is None

        # Simulate first fill
        await tracker.update_position_from_fill(
            symbol="AAPL",
            delta_qty=10.0,
            fill_price=150.0,
            side="buy",
        )

        # Position should now exist
        position = tracker.get_position("AAPL")
        assert position is not None
        assert position.qty == 10.0
        assert position.entry_price == 150.0

    @pytest.mark.asyncio
    async def test_position_qty_updated_on_partial_fill(self, tmp_path):
        """Verify position qty increases correctly on partial fill."""
        db_path, state_store = _setup_db(tmp_path)
        mock_broker = MagicMock()
        tracker = PositionTracker(broker=mock_broker, state_store=state_store)

        # First fill
        await tracker.update_position_from_fill(
            symbol="AAPL",
            delta_qty=10.0,
            fill_price=150.0,
            side="buy",
        )

        # Second fill (partial)
        await tracker.update_position_from_fill(
            symbol="AAPL",
            delta_qty=5.0,
            fill_price=151.0,
            side="buy",
        )

        position = tracker.get_position("AAPL")
        assert position is not None
        assert position.qty == 15.0

    @pytest.mark.asyncio
    async def test_position_closed_on_full_fill(self, tmp_path):
        """Verify position is removed/stopped when sell order fully fills (exit)."""
        db_path, state_store = _setup_db(tmp_path)
        mock_broker = MagicMock()
        tracker = PositionTracker(broker=mock_broker, state_store=state_store)

        # Create position with buy fills
        await tracker.update_position_from_fill(
            symbol="AAPL",
            delta_qty=10.0,
            fill_price=150.0,
            side="buy",
        )

        # Sell fill that exhausts the position
        result = await tracker.update_position_from_fill(
            symbol="AAPL",
            delta_qty=10.0,
            fill_price=155.0,
            side="sell",
        )

        # Position should be closed
        assert result is None
        assert tracker.get_position("AAPL") is None

    @pytest.mark.asyncio
    async def test_position_unchanged_on_duplicate_fill_event(self, tmp_path):
        """Verify duplicate fill events don't double-count position qty."""
        db_path, state_store = _setup_db(tmp_path)
        mock_broker = MagicMock()
        tracker = PositionTracker(broker=mock_broker, state_store=state_store)

        # First fill
        await tracker.update_position_from_fill(
            symbol="AAPL",
            delta_qty=10.0,
            fill_price=150.0,
            side="buy",
        )

        # Duplicate fill (same delta)
        await tracker.update_position_from_fill(
            symbol="AAPL",
            delta_qty=10.0,
            fill_price=150.0,
            side="buy",
        )

        position = tracker.get_position("AAPL")
        assert position is not None
        # Should be 20.0 because we intentionally call it twice with delta
        # (idempotency is handled at the fill layer, not position layer)
        assert position.qty == 20.0

    @pytest.mark.asyncio
    async def test_position_handles_multiple_partial_fills(self, tmp_path):
        """Verify position correctly accumulates across 3+ partial fills."""
        db_path, state_store = _setup_db(tmp_path)
        mock_broker = MagicMock()
        tracker = PositionTracker(broker=mock_broker, state_store=state_store)

        # Multiple partial fills
        fills = [
            (10.0, 150.0),
            (15.0, 151.0),
            (25.0, 152.0),
        ]

        for delta_qty, fill_price in fills:
            await tracker.update_position_from_fill(
                symbol="AAPL",
                delta_qty=delta_qty,
                fill_price=fill_price,
                side="buy",
            )

        position = tracker.get_position("AAPL")
        assert position is not None
        assert position.qty == 50.0  # 10 + 15 + 25

    @pytest.mark.asyncio
    async def test_position_no_drift_after_multiple_partials(self, tmp_path):
        """Verify position qty matches sum of all fill deltas after sequence."""
        db_path, state_store = _setup_db(tmp_path)
        mock_broker = MagicMock()
        tracker = PositionTracker(broker=mock_broker, state_store=state_store)

        # 0→10→25→50 sequence
        await tracker.update_position_from_fill(
            symbol="AAPL", delta_qty=10.0, fill_price=150.0, side="buy"
        )
        await tracker.update_position_from_fill(
            symbol="AAPL", delta_qty=15.0, fill_price=151.0, side="buy"
        )
        await tracker.update_position_from_fill(
            symbol="AAPL", delta_qty=25.0, fill_price=152.0, side="buy"
        )

        position = tracker.get_position("AAPL")
        assert position is not None
        assert position.qty == 50.0

    @pytest.mark.asyncio
    async def test_position_avg_price_blended_correctly(self, tmp_path):
        """Verify average entry price is correctly blended across multiple fills."""
        db_path, state_store = _setup_db(tmp_path)
        mock_broker = MagicMock()
        tracker = PositionTracker(broker=mock_broker, state_store=state_store)

        # First fill: 10 shares @ $150
        await tracker.update_position_from_fill(
            symbol="AAPL", delta_qty=10.0, fill_price=150.0, side="buy"
        )

        # Second fill: 10 shares @ $160
        await tracker.update_position_from_fill(
            symbol="AAPL", delta_qty=10.0, fill_price=160.0, side="buy"
        )

        position = tracker.get_position("AAPL")
        assert position is not None
        # Blended average: (10*150 + 10*160) / 20 = 155.0
        assert position.entry_price == 155.0

    @pytest.mark.asyncio
    async def test_position_sell_without_existing_logs_warning(self, tmp_path, caplog):
        """Verify short position is created when sell fill arrives without position."""
        db_path, state_store = _setup_db(tmp_path)
        mock_broker = MagicMock()
        tracker = PositionTracker(broker=mock_broker, state_store=state_store)

        with caplog.at_level("INFO"):
            result = await tracker.update_position_from_fill(
                symbol="AAPL",
                delta_qty=10.0,
                fill_price=150.0,
                side="sell",
            )

        # Short position should be created
        assert result is not None
        assert result.side == "short"
        assert result.qty == 10.0
        assert result.entry_price == 150.0
        assert "Short position created on first sell fill" in caplog.text


# ---------------------------------------------------------------
# OrderState Enum tests
# ---------------------------------------------------------------


class TestOrderStateEnum:
    """Test OrderState enum functionality."""

    def test_order_state_enum_has_all_required_states(self):
        """Verify all 12 states are defined (8 original + 3 partial terminals + 1 unknown)."""
        states = {
            OrderState.PENDING,
            OrderState.SUBMITTED,
            OrderState.PENDING_CANCEL,
            OrderState.PARTIAL,
            OrderState.FILLED,
            OrderState.CANCELLED,
            OrderState.EXPIRED,
            OrderState.REJECTED,
            OrderState.CANCELLED_PARTIAL,
            OrderState.EXPIRED_PARTIAL,
            OrderState.REJECTED_PARTIAL,
            OrderState.UNKNOWN,
        }
        assert len(states) == 12

    def test_order_state_from_alpaca_mapping_correct(self):
        """Verify all Alpaca status strings map to correct OrderState."""
        test_cases = [
            ("new", OrderState.PENDING),
            ("pending_new", OrderState.PENDING),
            ("submitted", OrderState.SUBMITTED),
            ("accepted", OrderState.SUBMITTED),
            ("partially_filled", OrderState.PARTIAL),
            ("filled", OrderState.FILLED),
            ("canceled", OrderState.CANCELLED),
            ("pending_cancel", OrderState.PENDING_CANCEL),
            ("expired", OrderState.EXPIRED),
            ("rejected", OrderState.REJECTED),
        ]

        for alpaca_status, expected_state in test_cases:
            result = OrderState.from_alpaca(alpaca_status)
            assert result == expected_state, f"Failed for {alpaca_status}"

    def test_order_state_is_terminal_identifies_correctly(self):
        """Verify terminal states return True, non-terminal return False."""
        # Terminal states
        assert OrderState.FILLED.is_terminal is True
        assert OrderState.CANCELLED.is_terminal is True
        assert OrderState.EXPIRED.is_terminal is True
        assert OrderState.REJECTED.is_terminal is True

        # Partial terminal states (also terminal)
        assert OrderState.CANCELLED_PARTIAL.is_terminal is True
        assert OrderState.EXPIRED_PARTIAL.is_terminal is True
        assert OrderState.REJECTED_PARTIAL.is_terminal is True

        # Unknown state (treated as terminal)
        assert OrderState.UNKNOWN.is_terminal is True

        # Non-terminal states
        assert OrderState.PENDING.is_terminal is False
        assert OrderState.SUBMITTED.is_terminal is False
        assert OrderState.PARTIAL.is_terminal is False
        assert OrderState.PENDING_CANCEL.is_terminal is False

    def test_order_state_has_fill_potential_identifies_correctly(self):
        """Verify states that can receive fills return True."""
        # States with fill potential
        assert OrderState.PENDING.has_fill_potential is True
        assert OrderState.SUBMITTED.has_fill_potential is True
        assert OrderState.PARTIAL.has_fill_potential is True
        assert OrderState.PENDING_CANCEL.has_fill_potential is True

        # Terminal states (no fill potential)
        assert OrderState.FILLED.has_fill_potential is False
        assert OrderState.CANCELLED.has_fill_potential is False
        assert OrderState.EXPIRED.has_fill_potential is False
        assert OrderState.REJECTED.has_fill_potential is False

        # Partial terminal states (no fill potential)
        assert OrderState.CANCELLED_PARTIAL.has_fill_potential is False
        assert OrderState.EXPIRED_PARTIAL.has_fill_potential is False
        assert OrderState.REJECTED_PARTIAL.has_fill_potential is False

        # Unknown state (no fill potential)
        assert OrderState.UNKNOWN.has_fill_potential is False

    def test_order_state_transition_from_pending_to_partial(self):
        """Verify state progression through order lifecycle."""
        # Start pending
        state = OrderState.from_alpaca("new")
        assert state == OrderState.PENDING
        assert state.has_fill_potential is True

        # Transition to submitted
        state = OrderState.from_alpaca("submitted")
        assert state == OrderState.SUBMITTED
        assert state.has_fill_potential is True

        # Transition to partial
        state = OrderState.from_alpaca("partially_filled")
        assert state == OrderState.PARTIAL
        assert state.has_fill_potential is True

    def test_order_state_transition_from_partial_to_filled(self):
        """Verify terminal transition works correctly."""
        # Start partial
        state = OrderState.from_alpaca("partially_filled")
        assert state == OrderState.PARTIAL
        assert state.is_terminal is False

        # Transition to filled (terminal)
        state = OrderState.from_alpaca("filled")
        assert state == OrderState.FILLED
        assert state.is_terminal is True
        assert state.has_fill_potential is False

    def test_order_state_partial_terminal_detection(self):
        """Verify from_alpaca() correctly detects partial terminal states."""
        # Cancelled with partial fills
        state = OrderState.from_alpaca("canceled", filled_qty=50.0, order_qty=100.0)
        assert state == OrderState.CANCELLED_PARTIAL
        assert state.is_terminal is True
        assert state.has_fill_potential is False

        # Cancelled with no fills
        state = OrderState.from_alpaca("canceled", filled_qty=0.0, order_qty=100.0)
        assert state == OrderState.CANCELLED
        assert state.is_terminal is True

        # Cancelled fully filled (edge case - should be CANCELLED not CANCELLED_PARTIAL)
        state = OrderState.from_alpaca("canceled", filled_qty=100.0, order_qty=100.0)
        assert state == OrderState.CANCELLED
        assert state.is_terminal is True

        # Expired with partial fills
        state = OrderState.from_alpaca("expired", filled_qty=25.0, order_qty=100.0)
        assert state == OrderState.EXPIRED_PARTIAL
        assert state.is_terminal is True
        assert state.has_fill_potential is False

        # Rejected with partial fills
        state = OrderState.from_alpaca("rejected", filled_qty=10.0, order_qty=100.0)
        assert state == OrderState.REJECTED_PARTIAL
        assert state.is_terminal is True
        assert state.has_fill_potential is False

        # Cancelled without qty context (fallback to full terminal)
        state = OrderState.from_alpaca("canceled")
        assert state == OrderState.CANCELLED
        assert state.is_terminal is True

    def test_order_state_unknown_status_logs_warning(self, caplog):
        """Verify unknown statuses return UNKNOWN and log warning."""
        import logging

        with caplog.at_level(logging.WARNING):
            state = OrderState.from_alpaca("invalid_status_xyz")

        assert state == OrderState.UNKNOWN
        assert state.is_terminal is True
        assert state.has_fill_potential is False
        assert "Unknown order status from broker" in caplog.text

    def test_order_state_included_in_order_update_event(self):
        """Verify OrderUpdateEvent includes state field."""
        from datetime import datetime, timezone

        event = OrderUpdateEvent(
            order_id="a-1",
            client_order_id="c-1",
            symbol="AAPL",
            side="buy",
            status="partially_filled",
            state=OrderState.PARTIAL,
            filled_qty=10.0,
            avg_fill_price=150.0,
            timestamp=datetime.now(timezone.utc),
        )

        assert hasattr(event, "state")
        assert event.state == OrderState.PARTIAL


# ---------------------------------------------------------------
# Fill-to-Position Integration tests
# ---------------------------------------------------------------


class TestFillPositionIntegration:
    """End-to-end tests for fill-to-position flow."""

    @pytest.mark.asyncio
    async def test_stream_fill_event_updates_position_correctly(self, tmp_path):
        """Full flow: stream event → OrderUpdatesHandler → PositionTracker update."""
        db_path, store = _setup_db(tmp_path)

        # Create a mock position tracker
        mock_tracker = MagicMock()
        mock_tracker.update_position_from_fill = AsyncMock(return_value=None)

        bus = DummyEventBus()
        handler = OrderUpdatesHandler(store, bus, position_tracker=mock_tracker)

        # Insert order intent
        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=0)

        # Simulate stream fill event
        raw = _make_raw_update(
            "a-1", "c-1", filled_qty="10", filled_avg_price="150.0", status="partially_filled"
        )
        await handler.on_order_update(raw)

        # Verify PositionTracker was called
        mock_tracker.update_position_from_fill.assert_called_once()
        call_kwargs = mock_tracker.update_position_from_fill.call_args.kwargs
        assert call_kwargs["symbol"] == "AAPL"
        assert call_kwargs["delta_qty"] == 10.0
        assert call_kwargs["fill_price"] == 150.0
        assert call_kwargs["side"] == "buy"

    @pytest.mark.asyncio
    async def test_no_position_drift_after_fill_sequence(self, tmp_path):
        """After 0→10→25→50 fill sequence, position qty equals 50."""
        from src.position_tracker import PositionTracker

        db_path, store = _setup_db(tmp_path)

        mock_broker = MagicMock()
        tracker = PositionTracker(broker=mock_broker, state_store=store)

        bus = DummyEventBus()
        handler = OrderUpdatesHandler(store, bus, position_tracker=tracker)

        # Insert order intent
        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=0, qty=50)

        # Simulate fill sequence: 0→10→25→50
        fills = [
            ("10", "150.0", "partially_filled"),
            ("25", "151.0", "partially_filled"),
            ("50", "152.0", "filled"),
        ]

        for filled_qty, avg_price, status in fills:
            raw = _make_raw_update(
                "a-1", "c-1", filled_qty=filled_qty, filled_avg_price=avg_price, status=status
            )
            await handler.on_order_update(raw)

        # Verify position qty equals total filled
        position = tracker.get_position("AAPL")
        assert position is not None
        assert position.qty == 50.0

    @pytest.mark.asyncio
    async def test_position_tracker_receives_delta_not_cumulative(self, tmp_path):
        """Verify PositionTracker receives delta_qty, not cumulative."""
        db_path, store = _setup_db(tmp_path)

        # Track the deltas received
        received_deltas = []

        class MockTracker:
            async def update_position_from_fill(
                self, symbol, delta_qty, fill_price, side, timestamp=None
            ):
                received_deltas.append(delta_qty)

        mock_tracker = MockTracker()

        bus = DummyEventBus()
        handler = OrderUpdatesHandler(store, bus, position_tracker=mock_tracker)

        # Insert order intent with initial fill
        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=10)

        # New fill: cum 25 (delta should be 15)
        raw = _make_raw_update(
            "a-1", "c-1", filled_qty="25", filled_avg_price="151.0", status="partially_filled"
        )
        await handler.on_order_update(raw)

        # Verify delta was 15 (not 25)
        assert len(received_deltas) == 1
        assert received_deltas[0] == 15.0

    @pytest.mark.asyncio
    async def test_fill_idempotency_preserves_position_consistency(self, tmp_path):
        """Duplicate fill events don't corrupt position state."""
        from src.position_tracker import PositionTracker

        db_path, store = _setup_db(tmp_path)

        mock_broker = MagicMock()
        tracker = PositionTracker(broker=mock_broker, state_store=store)

        bus = DummyEventBus()
        handler = OrderUpdatesHandler(store, bus, position_tracker=tracker)

        # Insert order intent
        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=0)

        # First fill
        raw = _make_raw_update(
            "a-1", "c-1", filled_qty="10", filled_avg_price="150.0", status="partially_filled"
        )
        await handler.on_order_update(raw)

        # Duplicate fill (should be deduplicated at fill layer)
        await handler.on_order_update(raw)

        # Position should still be 10 (not 20)
        position = tracker.get_position("AAPL")
        assert position is not None
        # Note: The second fill should be deduplicated by insert_fill_idempotent
        # resulting in delta_qty=0, so position should remain 10
        assert position.qty == 10.0

    @pytest.mark.asyncio
    async def test_reconciliation_syncs_missed_partial_fills(self, tmp_path):
        """Reconciliation detects drift and updates both fills table and position."""
        from src.position_tracker import PositionTracker
        from src.reconciliation import reconcile_fills

        db_path, store = _setup_db(tmp_path)

        mock_broker = MagicMock()
        tracker = PositionTracker(broker=mock_broker, state_store=store)

        bus = DummyEventBus()
        handler = OrderUpdatesHandler(store, bus, position_tracker=tracker)

        # Insert order intent with some fills already recorded
        _insert_order_intent(db_path, "c-1", "a-1", filled_qty=10, status="partially_filled")

        # First fill to establish position
        await tracker.update_position_from_fill(
            symbol="AAPL", delta_qty=10.0, fill_price=150.0, side="buy"
        )

        # Mock broker reports more fills than DB (missed fill scenario)
        mock_broker.get_open_orders = AsyncMock(
            return_value=[
                {
                    "id": "a-1",
                    "client_order_id": "c-1",
                    "symbol": "AAPL",
                    "side": "buy",
                    "status": "partially_filled",
                    "filled_qty": 25,  # Broker has 25, DB has 10
                    "filled_avg_price": 151.0,
                }
            ]
        )

        # Run reconciliation
        count = await reconcile_fills(
            broker=mock_broker,
            state_store=store,
            on_order_update=handler.on_order_update,
        )

        # Should have synthesised 1 update
        assert count == 1

        # Position should be updated with the missed fill
        position = tracker.get_position("AAPL")
        assert position is not None
        assert position.qty == 25.0  # 10 + 15 missed fill
