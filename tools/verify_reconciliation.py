#!/usr/bin/env python3
"""Verify reconciliation passes."""

import os
import sqlite3
import sys

sys.path.insert(0, "src")
from alpaca.trading.client import TradingClient

client = TradingClient(os.environ["ALPACA_API_KEY"], os.environ["ALPACA_SECRET_KEY"], paper=True)

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

# Get latest positions_snapshot
conn = sqlite3.connect("data/trades.db")
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
    sys.exit(1)
else:
    print("  ✓ All positions match!")
