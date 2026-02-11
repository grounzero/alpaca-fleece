"""Tests for order update polling in StreamPolling."""

import asyncio
import os
import sqlite3
from datetime import datetime, timezone
from enum import Enum
from unittest.mock import AsyncMock, MagicMock

import pytest

from src.stream_polling import StreamPolling


@pytest.fixture
def mock_stream(tmp_path):
    """Create a StreamPolling instance with mocked dependencies."""
    stream = StreamPolling(
        api_key="test_key",
        secret_key="test_secret",
        paper=True,
        feed="iex",
    )
    # Use file-based DB for tests (shared across connections)
    stream._db_path = str(tmp_path / "test_trades.db")
    stream.trading_client = MagicMock()
    stream.on_order_update = AsyncMock()

    # Create table immediately
    conn = sqlite3.connect(stream._db_path)
    cursor = conn.cursor()
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS order_intents (
            client_order_id TEXT PRIMARY KEY,
            symbol TEXT NOT NULL,
            side TEXT NOT NULL,
            qty NUMERIC(10, 4) NOT NULL,
            status TEXT NOT NULL,
            filled_qty NUMERIC(10, 4) DEFAULT 0,
            filled_avg_price NUMERIC(10, 4),
            alpaca_order_id TEXT,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        )
        """)
    conn.commit()
    conn.close()

    yield stream

    # Cleanup
    if os.path.exists(stream._db_path):
        os.unlink(stream._db_path)


class TestOrderUpdatePolling:
    """Test suite for order update polling functionality."""

    @pytest.mark.asyncio
    async def test_get_submitted_orders_returns_pending_orders(self, mock_stream):
        """_get_submitted_orders should return only non-terminal orders."""
        stream = mock_stream

        # Insert test orders
        conn = sqlite3.connect(stream._db_path)
        cursor = conn.cursor()
        now = datetime.now(timezone.utc).isoformat()

        # Non-terminal orders (should be returned)
        cursor.execute(
            """
            INSERT INTO order_intents
            (client_order_id, symbol, side, qty, status, alpaca_order_id, created_at_utc, updated_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            ("order-1", "AAPL", "buy", 100, "submitted", "alpaca-1", now, now),
        )
        cursor.execute(
            """
            INSERT INTO order_intents
            (client_order_id, symbol, side, qty, status, alpaca_order_id, created_at_utc, updated_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            ("order-2", "MSFT", "sell", 50, "partially_filled", "alpaca-2", now, now),
        )

        # Terminal orders (should NOT be returned)
        cursor.execute(
            """
            INSERT INTO order_intents
            (client_order_id, symbol, side, qty, status, alpaca_order_id, created_at_utc, updated_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            ("order-3", "GOOGL", "buy", 25, "filled", "alpaca-3", now, now),
        )
        cursor.execute(
            """
            INSERT INTO order_intents
            (client_order_id, symbol, side, qty, status, alpaca_order_id, created_at_utc, updated_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            ("order-4", "TSLA", "sell", 10, "cancelled", "alpaca-4", now, now),
        )
        conn.commit()
        conn.close()

        # Test
        orders = stream._get_submitted_orders()

        # Should return 2 orders (submitted and partially_filled)
        assert len(orders) == 2
        # Use dict keyed by client_order_id for order-independent assertion
        orders_by_id = {o["client_order_id"]: o for o in orders}
        assert "order-1" in orders_by_id
        assert orders_by_id["order-1"]["status"] == "submitted"
        assert "order-2" in orders_by_id
        assert orders_by_id["order-2"]["status"] == "partially_filled"

    @pytest.mark.asyncio
    async def test_check_order_status_emits_update_on_transition(self, mock_stream):
        """_check_order_status should emit update when status changes."""
        stream = mock_stream

        # Insert a submitted order
        conn = sqlite3.connect(stream._db_path)
        cursor = conn.cursor()
        now = datetime.now(timezone.utc).isoformat()
        cursor.execute(
            """
            INSERT INTO order_intents
            (client_order_id, symbol, side, qty, status, alpaca_order_id, created_at_utc, updated_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            ("test-order", "AAPL", "buy", 100, "submitted", "alpaca-123", now, now),
        )
        conn.commit()
        conn.close()

        # Mock Alpaca returning filled status
        mock_order = {
            "id": "alpaca-123",
            "status": "filled",
            "filled_qty": "100",
            "filled_avg_price": "150.00",
        }
        stream.trading_client.get_order_by_id.return_value = mock_order

        # Test
        await stream._check_order_status()

        # Should emit update
        assert stream.on_order_update.called
        update_event = stream.on_order_update.call_args[0][0]
        assert update_event.order.status.value == "filled"

    @pytest.mark.asyncio
    async def test_check_order_status_handles_enum_status(self, mock_stream):
        """_check_order_status should handle enum-like status objects with .value."""
        stream = mock_stream

        # Insert a submitted order
        conn = sqlite3.connect(stream._db_path)
        cursor = conn.cursor()
        now = datetime.now(timezone.utc).isoformat()
        cursor.execute(
            """
            INSERT INTO order_intents
            (client_order_id, symbol, side, qty, status, alpaca_order_id, created_at_utc, updated_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            ("test-order", "AAPL", "buy", 100, "submitted", "alpaca-123", now, now),
        )
        conn.commit()
        conn.close()

        # Mock Alpaca returning enum-like status
        class OrderStatus:
            FILLED = "filled"

        mock_status = MagicMock()
        mock_status.value = OrderStatus.FILLED

        mock_order = MagicMock()
        mock_order.id = "alpaca-123"
        mock_order.status = mock_status
        mock_order.filled_qty = "100"
        mock_order.filled_avg_price = "150.00"
        stream.trading_client.get_order_by_id.return_value = mock_order

        # Test
        await stream._check_order_status()

        # Should emit update with status
        assert stream.on_order_update.called
        update_event = stream.on_order_update.call_args[0][0]
        assert update_event.order.status.value == "filled"

    @pytest.mark.asyncio
    async def test_check_order_status_handles_stdlib_enum_status(self, mock_stream):
        """_check_order_status should handle stdlib enum.Enum status values."""
        stream = mock_stream

        # Insert a submitted order
        conn = sqlite3.connect(stream._db_path)
        cursor = conn.cursor()
        now = datetime.now(timezone.utc).isoformat()
        cursor.execute(
            """
            INSERT INTO order_intents
            (client_order_id, symbol, side, qty, status, alpaca_order_id, created_at_utc, updated_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            ("test-order", "AAPL", "buy", 100, "submitted", "alpaca-123", now, now),
        )
        conn.commit()
        conn.close()

        # Create a stdlib Enum for status
        class OrderStatus(Enum):
            FILLED = "filled"

        mock_order = MagicMock()
        mock_order.id = "alpaca-123"
        mock_order.status = OrderStatus.FILLED
        mock_order.filled_qty = "100"
        mock_order.filled_avg_price = "150.00"
        stream.trading_client.get_order_by_id.return_value = mock_order

        # Test
        await stream._check_order_status()

        # Should emit update with status
        assert stream.on_order_update.called
        update_event = stream.on_order_update.call_args[0][0]
        assert update_event.order.status.value == "filled"

    @pytest.mark.asyncio
    async def test_check_order_status_does_not_emit_duplicate(self, mock_stream):
        """_check_order_status should not emit duplicate updates for same transition."""
        stream = mock_stream

        # Insert a filled order (already filled in DB)
        conn = sqlite3.connect(stream._db_path)
        cursor = conn.cursor()
        now = datetime.now(timezone.utc).isoformat()
        cursor.execute(
            """
            INSERT INTO order_intents
            (client_order_id, symbol, side, qty, status, filled_qty, alpaca_order_id, created_at_utc, updated_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            ("test-order", "AAPL", "buy", 100, "filled", 100, "alpaca-123", now, now),
        )
        conn.commit()
        conn.close()

        # Mock Alpaca also returning filled status (same as DB)
        mock_order = {
            "id": "alpaca-123",
            "status": "filled",
            "filled_qty": "100",
            "filled_avg_price": "150.00",
        }
        stream.trading_client.get_order_by_id.return_value = mock_order

        # Reset mock
        stream.on_order_update.reset_mock()

        # Test
        await stream._check_order_status()

        # Should NOT emit update (status unchanged)
        assert not stream.on_order_update.called

    @pytest.mark.asyncio
    async def test_update_order_status_persists_to_db(self, mock_stream):
        """_update_order_status should persist changes to SQLite."""
        stream = mock_stream

        # Insert a submitted order
        conn = sqlite3.connect(stream._db_path)
        cursor = conn.cursor()
        now = datetime.now(timezone.utc).isoformat()
        cursor.execute(
            """
            INSERT INTO order_intents
            (client_order_id, symbol, side, qty, status, alpaca_order_id, created_at_utc, updated_at_utc)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            ("test-order", "AAPL", "buy", 100, "submitted", "alpaca-123", now, now),
        )
        conn.commit()
        conn.close()

        # Update status
        stream._update_order_status("test-order", "filled", 100, 150.00)

        # Verify in DB
        conn = sqlite3.connect(stream._db_path)
        cursor = conn.cursor()
        cursor.execute(
            "SELECT status, filled_qty FROM order_intents WHERE client_order_id = ?",
            ("test-order",),
        )
        row = cursor.fetchone()
        conn.close()

        assert row[0] == "filled"
        assert row[1] == 100

    def test_create_order_update_event_converts_dict_to_canonical(self):
        """_create_order_update_event should convert dict response to canonical form."""
        stream = StreamPolling("test_key", "test_secret")

        mock_order = {
            "id": "alpaca-123",
            "client_order_id": "test-order",
            "symbol": "AAPL",
            "status": "filled",
            "filled_qty": "100",
            "filled_avg_price": "150.00",
        }

        event = stream._create_order_update_event(mock_order)

        assert event.order.id == "alpaca-123"
        assert event.order.client_order_id == "test-order"
        assert event.order.symbol == "AAPL"
        assert event.order.status.value == "filled"
        assert event.order.filled_qty == "100"
        assert event.order.filled_avg_price == "150.00"

    def test_create_order_update_event_converts_object_to_canonical(self):
        """_create_order_update_event should convert Order object response to canonical form."""
        stream = StreamPolling("test_key", "test_secret")

        mock_order = MagicMock()
        mock_order.id = "alpaca-123"
        mock_order.client_order_id = "test-order"
        mock_order.symbol = "AAPL"
        mock_order.status = "filled"
        mock_order.filled_qty = 100
        mock_order.filled_avg_price = 150.00

        event = stream._create_order_update_event(mock_order)

        assert event.order.id == "alpaca-123"
        assert event.order.status.value == "filled"

    def test_create_order_update_event_converts_enum_status_to_canonical(self):
        """_create_order_update_event should convert enum status (with .value) to canonical form."""
        stream = StreamPolling("test_key", "test_secret")

        # Create a mock enum-like status object
        class OrderStatus:
            FILLED = "filled"

        mock_status = MagicMock()
        mock_status.value = OrderStatus.FILLED

        mock_order = MagicMock()
        mock_order.id = "alpaca-123"
        mock_order.client_order_id = "test-order"
        mock_order.symbol = "AAPL"
        mock_order.status = mock_status
        mock_order.filled_qty = 100
        mock_order.filled_avg_price = 150.00

        event = stream._create_order_update_event(mock_order)

        assert event.order.id == "alpaca-123"
        assert event.order.status.value == "filled"


def test_hex_to_uuid_conversion():
    """_hex_to_uuid should format 32-char hex strings into UUIDs and leave others unchanged."""
    stream = StreamPolling("test_key", "test_secret")

    # 32-char hex -> UUID format
    hex_id = "51057fad52fa6ca251057fad52fa6ca2"
    expected_uuid = f"{hex_id[:8]}-{hex_id[8:12]}-{hex_id[12:16]}-{hex_id[16:20]}-{hex_id[20:]}"
    assert stream._hex_to_uuid(hex_id) == expected_uuid

    # Already uuid -> unchanged
    uuid_id = expected_uuid
    assert stream._hex_to_uuid(uuid_id) == uuid_id

    # Short string -> unchanged
    short = "abc123"
    assert stream._hex_to_uuid(short) == short


@pytest.mark.asyncio
async def test_poll_order_updates_runs_continuously(mock_stream, monkeypatch):
    """_poll_order_updates should run continuously and call _check_order_status."""
    stream = mock_stream

    # Mock _check_order_status to track calls
    call_count = 0

    async def mock_check():
        nonlocal call_count
        call_count += 1
        if call_count >= 2:
            # Cancel after 2 calls
            raise asyncio.CancelledError()

    stream._check_order_status = mock_check

    # Patch asyncio.sleep to avoid delays in tests
    async def mock_sleep(seconds):
        pass

    monkeypatch.setattr(asyncio, "sleep", mock_sleep)

    # Test
    with pytest.raises(asyncio.CancelledError):
        await stream._poll_order_updates()

    # Should have been called at least twice
    assert call_count >= 2
