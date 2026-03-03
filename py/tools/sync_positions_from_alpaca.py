#!/usr/bin/env python3
"""Sync position_tracking table from Alpaca positions.

This script:
1. Creates a backup of the position_tracking table
2. Queries Alpaca for current positions
3. Clears the position_tracking table
4. Rebuilds position_tracking from Alpaca data
5. Reports discrepancies and changes
"""

import argparse
import os
import sqlite3
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional

from alpaca.trading.client import TradingClient


def _quote_ident(name: str) -> str:
    """Return a safely-quoted SQLite identifier (double-quote with embedded quotes escaped)."""
    return '"' + name.replace('"', '""') + '"'


def _get_bool_env(name: str, default: bool) -> bool:
    """Parse a boolean environment variable.

    Accepts 1/true/yes/y/on (case-insensitive) as truthy values.
    """
    value = os.environ.get(name)
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "y", "on"}


# Load credentials from environment
API_KEY = os.environ.get("ALPACA_API_KEY")
SECRET_KEY = os.environ.get("ALPACA_SECRET_KEY")
PAPER = _get_bool_env("ALPACA_PAPER", True)
DATABASE_PATH = os.environ.get("DATABASE_PATH", "data/trades.db")


def get_sqlite_positions(conn: sqlite3.Connection) -> dict:
    """Get all positions from SQLite position_tracking table."""
    cursor = conn.cursor()
    cursor.execute("""
        SELECT symbol, side, qty, entry_price, entry_time 
        FROM position_tracking
    """)
    positions = {}
    for row in cursor.fetchall():
        symbol, side, qty, entry_price, entry_time = row
        positions[symbol] = {
            "symbol": symbol,
            "side": side,
            "qty": float(qty),
            "entry_price": float(entry_price),
            "entry_time": entry_time,
        }
    return positions


def backup_position_tracking(conn: sqlite3.Connection) -> Optional[str]:
    """Create backup of position_tracking table."""
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
    backup_table = f"position_tracking_backup_{timestamp}"

    cursor = conn.cursor()

    # Check if table exists
    cursor.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='position_tracking'")
    if not cursor.fetchone():
        print("  position_tracking table does not exist yet")
        return None

    # Create backup table (quote identifiers to avoid injection)
    cursor.execute(
        f"CREATE TABLE {_quote_ident(backup_table)} AS SELECT * FROM {_quote_ident('position_tracking')}"
    )
    conn.commit()

    # Count records
    cursor.execute(f"SELECT COUNT(*) FROM {_quote_ident(backup_table)}")
    count = cursor.fetchone()[0]

    print(f"  ✓ Backup created: {backup_table} ({count} records)")
    return backup_table


def fetch_alpaca_positions(client: TradingClient) -> dict:
    """Fetch all positions from Alpaca."""
    print("\n→ Fetching positions from Alpaca...")

    positions = client.get_all_positions()

    result = {}
    for pos in positions:
        symbol = pos.symbol
        qty = float(pos.qty)
        side = "long" if qty > 0 else "short"

        result[symbol] = {
            "symbol": symbol,
            "side": side,
            "qty": abs(qty),
            "entry_price": float(pos.avg_entry_price) if pos.avg_entry_price else 0.0,
            "market_price": float(pos.current_price) if pos.current_price else 0.0,
            "market_value": float(pos.market_value) if pos.market_value else 0.0,
        }

    print(f"  ✓ Found {len(result)} positions in Alpaca")
    return result


def compare_positions(sqlite_pos: dict, alpaca_pos: dict) -> dict:
    """Compare SQLite and Alpaca positions."""
    discrepancies = []

    sqlite_symbols = set(sqlite_pos.keys())
    alpaca_symbols = set(alpaca_pos.keys())

    # In SQLite but not in Alpaca
    only_in_sqlite = sqlite_symbols - alpaca_symbols
    for symbol in only_in_sqlite:
        discrepancies.append(
            {
                "type": "in_sqlite_not_alpaca",
                "symbol": symbol,
                "sqlite_qty": sqlite_pos[symbol]["qty"],
                "sqlite_side": sqlite_pos[symbol]["side"],
                "alpaca_qty": 0,
            }
        )

    # In Alpaca but not in SQLite
    only_in_alpaca = alpaca_symbols - sqlite_symbols
    for symbol in only_in_alpaca:
        discrepancies.append(
            {
                "type": "in_alpaca_not_sqlite",
                "symbol": symbol,
                "sqlite_qty": 0,
                "alpaca_qty": alpaca_pos[symbol]["qty"],
                "alpaca_side": alpaca_pos[symbol]["side"],
            }
        )

    # In both but different quantities
    common_symbols = sqlite_symbols & alpaca_symbols
    for symbol in common_symbols:
        sqlite_qty = sqlite_pos[symbol]["qty"]
        alpaca_qty = alpaca_pos[symbol]["qty"]
        if abs(sqlite_qty - alpaca_qty) > 0.0001:
            discrepancies.append(
                {
                    "type": "qty_mismatch",
                    "symbol": symbol,
                    "sqlite_qty": sqlite_qty,
                    "alpaca_qty": alpaca_qty,
                    "sqlite_side": sqlite_pos[symbol]["side"],
                    "alpaca_side": alpaca_pos[symbol]["side"],
                }
            )

    return {
        "discrepancies": discrepancies,
        "only_in_sqlite": list(only_in_sqlite),
        "only_in_alpaca": list(only_in_alpaca),
        "common": list(common_symbols),
    }


def clear_position_tracking(conn: sqlite3.Connection) -> int:
    """Clear all records from position_tracking table."""
    cursor = conn.cursor()
    cursor.execute("SELECT COUNT(*) FROM position_tracking")
    count = cursor.fetchone()[0]

    cursor.execute("DELETE FROM position_tracking")
    conn.commit()
    print(f"  ✓ position_tracking cleared ({count} records deleted)")
    return count


def rebuild_position_tracking(conn: sqlite3.Connection, alpaca_positions: dict) -> int:
    """Rebuild position_tracking table from Alpaca positions."""
    cursor = conn.cursor()

    inserted = 0
    now = datetime.now(timezone.utc).isoformat()

    for symbol, pos in alpaca_positions.items():
        cursor.execute(
            """INSERT INTO position_tracking 
               (symbol, side, qty, entry_price, atr, entry_time, extreme_price,
                trailing_stop_price, trailing_stop_activated, pending_exit, updated_at)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            (
                symbol,
                pos["side"],
                pos["qty"],
                pos["entry_price"],
                None,  # atr
                now,  # entry_time (using current time since we don't have original)
                pos["entry_price"],  # extreme_price defaults to entry_price
                None,  # trailing_stop_price
                0,  # trailing_stop_activated
                0,  # pending_exit
                now,
            ),
        )
        inserted += 1

    conn.commit()
    print(f"  ✓ Inserted {inserted} positions into position_tracking")
    return inserted


def init_schema(conn: sqlite3.Connection) -> None:
    """Create position_tracking table if not exists."""
    cursor = conn.cursor()
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS position_tracking (
            symbol TEXT PRIMARY KEY,
            side TEXT NOT NULL,
            qty NUMERIC(10, 4) NOT NULL,
            entry_price NUMERIC(10, 4) NOT NULL,
            atr NUMERIC(10, 4),
            entry_time TEXT NOT NULL,
            extreme_price NUMERIC(10,4) NOT NULL,
            trailing_stop_price NUMERIC(10, 4),
            trailing_stop_activated INTEGER DEFAULT 0,
            pending_exit INTEGER DEFAULT 0,
            updated_at TEXT NOT NULL
        )
    """)
    conn.commit()


def main() -> None:
    """Main entry point."""
    parser = argparse.ArgumentParser(description="Sync position_tracking from Alpaca")
    parser.add_argument(
        "--dry-run", action="store_true", help="Show what would change without modifying database"
    )
    parser.add_argument(
        "--no-backup", action="store_true", help="Skip creating backup (not recommended)"
    )
    args = parser.parse_args()

    print("=" * 60)
    print("Sync position_tracking from Alpaca")
    print("=" * 60)

    if args.dry_run:
        print("\n*** DRY RUN MODE - No changes will be made ***\n")

    # Validate credentials
    if not API_KEY or not SECRET_KEY:
        print("\n✗ Error: ALPACA_API_KEY and ALPACA_SECRET_KEY must be set")
        sys.exit(1)

    print(f"\nDatabase: {DATABASE_PATH}")
    print(f"Paper trading: {PAPER}")

    # Connect to database
    db_path = Path(DATABASE_PATH)
    if not db_path.exists():
        print(f"\n✗ Error: Database not found at {DATABASE_PATH}")
        sys.exit(1)

    conn = sqlite3.connect(DATABASE_PATH)

    # Ensure schema exists
    init_schema(conn)

    # Get current SQLite positions
    print("\n→ Checking current SQLite positions...")
    sqlite_positions = get_sqlite_positions(conn)
    print(f"  Found {len(sqlite_positions)} positions in SQLite")

    if sqlite_positions:
        print("\n  Current SQLite positions:")
        for symbol, pos in sorted(sqlite_positions.items()):
            print(f"    {symbol:6} {pos['side']:5} {pos['qty']:8.2f} @ ${pos['entry_price']:.2f}")

    # Connect to Alpaca
    print("\n→ Connecting to Alpaca...")
    try:
        client = TradingClient(API_KEY, SECRET_KEY, paper=PAPER)
        account = client.get_account()
        print(f"  ✓ Connected (Account: {account.account_number}, Equity: ${account.equity})")
    except Exception as e:
        print(f"\n✗ Error connecting to Alpaca: {e}")
        sys.exit(1)

    # Fetch Alpaca positions
    alpaca_positions = fetch_alpaca_positions(client)

    if alpaca_positions:
        print("\n  Current Alpaca positions:")
        for symbol, pos in sorted(alpaca_positions.items()):
            print(
                f"    {symbol:6} {pos['side']:5} {pos['qty']:8.2f} @ ${pos['entry_price']:.2f} (market: ${pos['market_price']:.2f})"
            )
    else:
        print("\n  No positions in Alpaca (account is flat)")

    # Compare positions
    print("\n→ Comparing positions...")
    comparison = compare_positions(sqlite_positions, alpaca_positions)

    if comparison["discrepancies"]:
        print(f"\n  ⚠ Found {len(comparison['discrepancies'])} discrepancies:")
        for d in comparison["discrepancies"]:
            if d["type"] == "in_sqlite_not_alpaca":
                print(
                    f"    - {d['symbol']}: In SQLite ({d['sqlite_side']} {d['sqlite_qty']}) but NOT in Alpaca"
                )
            elif d["type"] == "in_alpaca_not_sqlite":
                print(
                    f"    - {d['symbol']}: In Alpaca ({d['alpaca_side']} {d['alpaca_qty']}) but NOT in SQLite"
                )
            elif d["type"] == "qty_mismatch":
                print(
                    f"    - {d['symbol']}: Qty mismatch - SQLite: {d['sqlite_qty']}, Alpaca: {d['alpaca_qty']}"
                )
    else:
        print("  ✓ Positions are in sync - no discrepancies")

    if args.dry_run:
        print("\n" + "=" * 60)
        print("DRY RUN SUMMARY")
        print("=" * 60)
        print(f"SQLite positions:   {len(sqlite_positions)}")
        print(f"Alpaca positions:   {len(alpaca_positions)}")
        print(f"Discrepancies:      {len(comparison['discrepancies'])}")
        print("\n*** No changes made (dry-run mode) ***")
        conn.close()
        sys.exit(0)

    # Confirm before proceeding
    if comparison["discrepancies"]:
        print("\n→ Will sync position_tracking to match Alpaca:")
        print("    - Backup existing positions")
        print("    - Clear position_tracking table")
        print(f"    - Insert {len(alpaca_positions)} positions from Alpaca")
    else:
        print("\n→ Positions already in sync, no changes needed")
        conn.close()
        sys.exit(0)

    # Create backup
    if not args.no_backup:
        print("\n→ Creating backup...")
        backup_table = backup_position_tracking(conn)

    # Clear and rebuild
    print("\n→ Clearing position_tracking...")
    _ = clear_position_tracking(conn)  # noqa: F841

    print("\n→ Rebuilding from Alpaca positions...")
    _ = rebuild_position_tracking(conn, alpaca_positions)  # noqa: F841

    # Verify
    print("\n→ Verifying rebuild...")
    new_positions = get_sqlite_positions(conn)

    if len(new_positions) == len(alpaca_positions):
        print(f"  ✓ Position count matches: {len(new_positions)}")
    else:
        print(
            f"  ✗ Position count mismatch: expected {len(alpaca_positions)}, got {len(new_positions)}"
        )

    # Final comparison
    final_comparison = compare_positions(new_positions, alpaca_positions)

    print("\n" + "=" * 60)
    print("SYNC SUMMARY")
    print("=" * 60)
    print(f"SQLite positions (before): {len(sqlite_positions)}")
    print(f"Alpaca positions:          {len(alpaca_positions)}")
    print(f"SQLite positions (after):  {len(new_positions)}")
    print(f"Discrepancies resolved:    {len(comparison['discrepancies'])}")

    if final_comparison["discrepancies"]:
        print(f"\n  ⚠ Remaining discrepancies: {len(final_comparison['discrepancies'])}")
        for d in final_comparison["discrepancies"]:
            print(f"    - {d}")
        print("\n✗ SYNC INCOMPLETE - Discrepancies remain")
        conn.close()
        sys.exit(1)
    else:
        print("\n  ✓ Positions now in sync!")
        print("\n" + "=" * 60)
        print("✓ SYNC COMPLETE")
        print("=" * 60)
        if not args.no_backup and backup_table:
            print(f"Backup: {backup_table}")

    conn.close()


if __name__ == "__main__":
    main()
