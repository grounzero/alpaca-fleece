import sqlite3
from datetime import datetime

from src.adapters.persistence.mappers import (
    fill_from_row,
    order_intent_from_row,
    position_from_row,
)
from src.models.persistence import Fill, OrderIntent, Position


def test_order_intent_from_tuple():
    tup = (
        "cid123",
        "mystrategy",
        "AAPL",
        "buy",
        10,
        0.5,
        "submitted",
        2.0,
        150.5,
        "alpaca-1",
    )

    oi = order_intent_from_row(tup)
    assert isinstance(oi, OrderIntent)
    assert oi.client_order_id == "cid123"
    assert oi.strategy == "mystrategy"
    assert oi.symbol == "AAPL"
    assert oi.side == "buy"
    assert oi.qty == 10.0
    assert oi.atr == 0.5
    assert oi.status == "submitted"
    assert oi.filled_qty == 2.0
    assert oi.filled_avg_price == 150.5
    assert oi.alpaca_order_id == "alpaca-1"


def test_order_intent_from_sqliterow():
    conn = sqlite3.connect(":memory:")
    conn.row_factory = sqlite3.Row
    cur = conn.cursor()
    cur.execute(
        "CREATE TABLE order_intents(client_order_id TEXT, strategy TEXT, symbol TEXT, side TEXT, qty NUMERIC, atr NUMERIC, status TEXT, filled_qty NUMERIC, filled_avg_price NUMERIC, alpaca_order_id TEXT)"
    )
    cur.execute(
        "INSERT INTO order_intents VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        ("cid2", "s2", "TSLA", "sell", 5, None, "new", None, None, None),
    )
    conn.commit()
    cur.execute(
        "SELECT client_order_id, strategy, symbol, side, qty, atr, status, filled_qty, filled_avg_price, alpaca_order_id FROM order_intents LIMIT 1"
    )
    row = cur.fetchone()
    oi = order_intent_from_row(row)
    assert oi.client_order_id == "cid2"
    assert oi.symbol == "TSLA"
    assert oi.qty == 5.0
    assert oi.atr is None


def test_position_from_tuple_and_sqliterow():
    tup = ("AAPL", "long", 3, 100.0, 1.2, datetime.utcnow().isoformat(), 105.0, None, 0, 0, None)
    p = position_from_row(tup)
    assert isinstance(p, Position)
    assert p.symbol == "AAPL"
    assert p.side == "long"
    assert p.qty == 3.0

    # sqlite3.Row path
    conn = sqlite3.connect(":memory:")
    conn.row_factory = sqlite3.Row
    cur = conn.cursor()
    cur.execute(
        "CREATE TABLE position_tracking(symbol TEXT, side TEXT, qty NUMERIC, entry_price NUMERIC, atr NUMERIC, entry_time TEXT, extreme_price NUMERIC, trailing_stop_price NUMERIC, trailing_stop_activated INTEGER, pending_exit INTEGER, updated_at TEXT)"
    )
    now_iso = datetime.utcnow().isoformat()
    cur.execute(
        "INSERT INTO position_tracking VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        ("AAPL", "long", 2, 99.5, 1.5, now_iso, 101.0, None, 0, 0, now_iso),
    )
    conn.commit()
    cur.execute(
        "SELECT symbol, side, qty, entry_price, atr, entry_time, extreme_price, trailing_stop_price, trailing_stop_activated, pending_exit, updated_at FROM position_tracking LIMIT 1"
    )
    row = cur.fetchone()
    p2 = position_from_row(row)
    assert p2.symbol == "AAPL"
    assert p2.entry_price == 99.5


def test_fill_from_tuple():
    tup = (
        "aid",
        "cid",
        "AAPL",
        "buy",
        1.0,
        1.0,
        150.0,
        datetime.utcnow().isoformat(),
        "fid",
        1,
        "CUM:1",
        150.0,
    )
    f = fill_from_row(tup)
    assert isinstance(f, Fill)
    assert f.alpaca_order_id == "aid"
    assert f.client_order_id == "cid"
    assert f.delta_qty == 1.0
