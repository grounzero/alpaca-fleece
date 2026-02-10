#!/usr/bin/env python3
"""Update positions_snapshot to match Alpaca.

This inserts a new positions_snapshot record with current Alpaca positions
to allow reconciliation to pass on next bot startup.
"""

import os
import sqlite3
import sys
from datetime import datetime, timezone

sys.path.insert(0, "src")
from alpaca.trading.client import TradingClient

API_KEY = os.environ["ALPACA_API_KEY"]
SECRET_KEY = os.environ["ALPACA_SECRET_KEY"]
PAPER = os.environ.get("ALPACA_PAPER", "true").lower() == "true"
DATABASE_PATH = os.environ.get("DATABASE_PATH", "data/trades.db")

print("=" * 60)
print("Update positions_snapshot from Alpaca")
print("=" * 60)

# Connect to Alpaca
print("\n→ Connecting to Alpaca...")
client = TradingClient(API_KEY, SECRET_KEY, paper=PAPER)
account = client.get_account()
print(f"  ✓ Connected (Account: {account.account_number})")

# Fetch positions
print("\n→ Fetching positions from Alpaca...")
positions = client.get_all_positions()
print(f"  Found {len(positions)} positions")

# Connect to database
conn = sqlite3.connect(DATABASE_PATH)
cursor = conn.cursor()

# Insert new snapshot
now_utc = datetime.now(timezone.utc).isoformat()
print(f"\n→ Inserting new snapshot at {now_utc}...")

for pos in positions:
    qty = float(pos.qty)
    entry = float(pos.avg_entry_price) if pos.avg_entry_price else 0.0
    cursor.execute(
        """INSERT INTO positions_snapshot (timestamp_utc, symbol, qty, avg_entry_price)
           VALUES (?, ?, ?, ?)""",
        (now_utc, pos.symbol, qty, entry),
    )
    print(f"  {pos.symbol}: qty={qty}, entry=${entry:.2f}")

conn.commit()
conn.close()

print("\n" + "=" * 60)
print(f"✓ Inserted {len(positions)} positions into positions_snapshot")
print("=" * 60)
print("\nReconciliation should now pass on bot startup!")
