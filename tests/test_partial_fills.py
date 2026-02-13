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
    async def test_different_fill_ids_produce_separate_rows(self, tmp_path):
        """Different fill_ids for same cum_qty should produce separate rows."""
        db_path, store = _setup_db(tmp_path)

        # Insert two fills with different fill_ids but same cum_qty
        inserted1 = store.insert_fill_idempotent(
            alpaca_order_id="a-1",
            client_order_id="c-1",
            symbol="AAPL",
            side="buy",
            delta_qty=5,
            cum_qty=10,
            cum_avg_price=150.0,
            timestamp_utc="2024-01-01T00:00:00Z",
            fill_id="fill-1",
        )
        inserted2 = store.insert_fill_idempotent(
            alpaca_order_id="a-1",
            client_order_id="c-1",
            symbol="AAPL",
            side="buy",
            delta_qty=5,
            cum_qty=10,
            cum_avg_price=150.0,
            timestamp_utc="2024-01-01T00:00:01Z",
            fill_id="fill-2",
        )
        assert inserted1
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
        assert row[0] == 2
