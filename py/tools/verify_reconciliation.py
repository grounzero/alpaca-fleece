#!/usr/bin/env python3
"""Verify that Alpaca positions match the latest positions_snapshot in SQLite.

This script:
- Honors the `ALPACA_PAPER` environment variable to select paper/live mode.
- Fetches current positions from Alpaca.
- Loads the most recent snapshot from the `positions_snapshot` table.
- Compares quantities per symbol and exits with a non-zero status on mismatch.
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

    # Validate required Alpaca credentials before creating the client
    required_env_vars = ["ALPACA_API_KEY", "ALPACA_SECRET_KEY"]
    missing = [name for name in required_env_vars if not os.getenv(name)]
    if missing:
        missing_list = ", ".join(missing)
        print(
            f"Error: Missing required environment variable(s): {missing_list}.\n"
            "Set them in your shell, for example:\n"
            "  export ALPACA_API_KEY='<your_api_key>'\n"
            "  export ALPACA_SECRET_KEY='<your_secret_key>'",
            file=sys.stderr,
        )
        return 1

    api_key = os.getenv("ALPACA_API_KEY")
    secret_key = os.getenv("ALPACA_SECRET_KEY")

    client = TradingClient(
        api_key,
        secret_key,
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
            # Coerce to float for reliable comparison/printing
            qty = float(row[1]) if row[1] is not None else 0.0
            avg_entry_price = float(row[2]) if row[2] is not None else 0.0
            sqlite_positions[row[0]] = {"qty": qty, "avg_entry_price": avg_entry_price}

    print("\nSQLite positions_snapshot (latest):")
    for sym in sorted(sqlite_positions.keys()):
        d = sqlite_positions[sym]
        print(f'  {sym}: qty={d["qty"]:.4f}, entry=${d["avg_entry_price"]:.4f}')

    # Check for discrepancies
    print("\nReconciliation check:")
    discrepancies = []
    all_symbols = set(alpaca_positions.keys()) | set(sqlite_positions.keys())
    for sym in all_symbols:
        alpaca_qty = float(alpaca_positions.get(sym, {}).get("qty", 0))
        sqlite_qty = float(sqlite_positions.get(sym, {}).get("qty", 0))
        if abs(alpaca_qty - sqlite_qty) > 0.0001:
            discrepancies.append(f"{sym}: SQLite={sqlite_qty:.4f}, Alpaca={alpaca_qty:.4f}")

    if discrepancies:
        print("  ✗ DISCREPANCIES FOUND:")
        for d in discrepancies:
            print(f"    - {d}")
        return 1

    print("  ✓ All positions match!")
    return 0


if __name__ == "__main__":
    sys.exit(main())
