#!/usr/bin/env python3
"""Verify reconciliation passes.

This script resolves the `src` directory relative to the script location
and honors the `ALPACA_PAPER` environment variable to select paper/live mode.
"""

import os
import sqlite3
import sys

# Import Alpaca SDK normally (do not mutate sys.path)
from alpaca.trading.client import TradingClient

def main() -> int:
    # Determine paper/live mode from env; default to paper=True if unset for safety
    paper_env = os.getenv("ALPACA_PAPER")
    if paper_env is None:
        paper = True
    else:
        paper = paper_env.strip().lower() in ("1", "true", "yes", "y")

    client = TradingClient(
        os.environ["ALPACA_API_KEY"],
        os.environ["ALPACA_SECRET_KEY"],
        paper=paper,
    )

    # Get Alpaca positions
    alpaca_positions = {}
    for p in client.get_all_positions():
        alpaca_positions[p.symbol] = {
            "qty": float(p.qty),
            "avg_entry_price": float(p.avg_entry_price) if p.avg_entry_price else 0.0,
        }

    print("Alpaca positions:")
    for sym in sorted(alpaca_positions.keys()):
        d = alpaca_positions[sym]
        print(f'  {sym}: qty={d["qty"]}, entry=${d["avg_entry_price"]}')

    # Get latest positions_snapshot using context manager for proper cleanup
    db_path = os.getenv("DATABASE_PATH", "data/trades.db")
    with sqlite3.connect(db_path) as conn:
        cursor = conn.cursor()
        cursor.execute("""
            SELECT symbol, qty, avg_entry_price FROM positions_snapshot
            WHERE timestamp_utc = (SELECT MAX(timestamp_utc) FROM positions_snapshot)
        """)
        sqlite_positions = {}
        for row in cursor.fetchall():
            sqlite_positions[row[0]] = {"qty": row[1], "avg_entry_price": row[2]}

    print("\nSQLite positions_snapshot (latest):")
    for sym in sorted(sqlite_positions.keys()):
        d = sqlite_positions[sym]
        print(f'  {sym}: qty={d["qty"]}, entry=${d["avg_entry_price"]}')

    # Check for discrepancies
    print("\nReconciliation check:")
    discrepancies = []
    all_symbols = set(alpaca_positions.keys()) | set(sqlite_positions.keys())
    for sym in all_symbols:
        alpaca_qty = alpaca_positions.get(sym, {}).get("qty", 0)
        sqlite_qty = sqlite_positions.get(sym, {}).get("qty", 0)
        if abs(alpaca_qty - sqlite_qty) > 0.0001:
            discrepancies.append(f"{sym}: SQLite={sqlite_qty}, Alpaca={alpaca_qty}")

    if discrepancies:
        print("  ✗ DISCREPANCIES FOUND:")
        for d in discrepancies:
            print(f"    - {d}")
        return 1

    print("  ✓ All positions match!")
    return 0


if __name__ == "__main__":
    sys.exit(main())
