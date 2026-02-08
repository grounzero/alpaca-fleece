import sqlite3
from datetime import datetime, timezone
from decimal import Decimal


from src.state_store import StateStore
from src.position_tracker import PositionTracker, PositionData


class DummyBroker:
    pass


def test_load_persisted_positions_normalizes_numeric_types(tmp_path):
    db_file = tmp_path / "positions.db"

    ss = StateStore(db_path=str(db_file))

    # ensure schema exists
    pt = PositionTracker(broker=DummyBroker(), state_store=ss)
    pt.init_schema()

    now = datetime.now(timezone.utc).isoformat()

    # Insert a row with mixed numeric types (strings and Decimal)
    conn = sqlite3.connect(db_file)
    cur = conn.cursor()
    cur.execute(
        "INSERT OR REPLACE INTO position_tracking (symbol, side, qty, entry_price, atr, entry_time, highest_price, trailing_stop_price, trailing_stop_activated, pending_exit, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        (
            "ABC",
            "long",
            "2.0000",  # qty as string
            str(Decimal("123.45")),  # entry_price as string to emulate Decimal storage
            "1.2500",  # atr as string
            now,
            str(Decimal("125.00")),
            None,
            0,
            0,
            now,
        ),
    )
    conn.commit()
    conn.close()

    loaded = pt.load_persisted_positions()

    assert len(loaded) == 1
    p = loaded[0]
    assert isinstance(p, PositionData)
    # Numeric fields should be floats
    assert isinstance(p.qty, float)
    assert p.qty == 2.0
    assert isinstance(p.entry_price, float)
    assert abs(p.entry_price - 123.45) < 1e-9
    assert isinstance(p.atr, float)
    assert abs(p.atr - 1.25) < 1e-9


def test_start_tracking_persists_atr(tmp_path, monkeypatch):
    db_file = tmp_path / "positions2.db"
    ss = StateStore(db_path=str(db_file))

    pt = PositionTracker(broker=DummyBroker(), state_store=ss)

    captured = {}

    def fake_persist(position):
        captured["position"] = position

    monkeypatch.setattr(pt, "_persist_position", fake_persist)

    pos = pt.start_tracking(symbol="ZZZ", fill_price=50.0, qty=3.0, side="long", atr=2.71)

    # Ensure _persist_position was called with a PositionData containing atr
    assert "position" in captured
    persisted = captured["position"]
    assert isinstance(persisted, PositionData)
    assert persisted.atr == 2.71
    # And the returned PositionData also has atr
    assert pos.atr == 2.71
