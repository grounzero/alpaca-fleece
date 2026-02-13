#!/usr/bin/env python3
"""Get current trading status from Alpaca API.

This script provides consistent position data by querying Alpaca API directly,
avoiding the inconsistency between positions_snapshot (stale) and
position_tracking (runtime) tables.

Usage:
    python get_trading_status.py

Returns JSON with:
    - positions: list of current positions from Alpaca
    - position_count: number of open positions
    - equity: current account equity
    - open_orders: list of open orders
"""

import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path

# Add repo to path for imports
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_DIR = os.path.dirname(SCRIPT_DIR)
sys.path.insert(0, str(REPO_DIR))

from alpaca.trading.client import TradingClient  # noqa: E402
from dotenv import load_dotenv  # noqa: E402 - repo path added to sys.path above


def load_env_file():
    """Load environment variables from .env using python-dotenv.

    Use the same `load_dotenv()` behaviour as the rest of the codebase
    (see `src/config.py`) which handles common edge-cases like quoted
    values, escaped characters and multiline values. This replaces the
    ad-hoc parser which could mis-handle complex .env files.
    """
    env_path = Path(REPO_DIR) / ".env"
    if env_path.exists():
        # load_dotenv is best-effort and will not overwrite existing env vars
        load_dotenv(dotenv_path=str(env_path))


def get_trading_status():
    """Get current trading status from Alpaca API."""
    # Load environment from .env file
    load_env_file()

    # Load credentials from environment
    api_key = os.environ.get("ALPACA_API_KEY")
    secret_key = os.environ.get("ALPACA_SECRET_KEY")
    paper = os.environ.get("ALPACA_PAPER", "true").strip().lower() in (
        "1",
        "true",
        "yes",
        "y",
        "on",
    )

    if not api_key or not secret_key:
        return {"error": "Missing ALPACA_API_KEY or ALPACA_SECRET_KEY environment variables"}

    # Connect to Alpaca
    client = TradingClient(api_key, secret_key, paper=paper)

    # Get account info
    account = client.get_account()

    # Get positions from Alpaca API (authoritative source)
    positions = client.get_all_positions()
    position_list = []
    for pos in positions:
        position_list.append(
            {
                "symbol": pos.symbol,
                "qty": float(pos.qty),
                "side": "long" if float(pos.qty) > 0 else "short",
                "avg_entry_price": float(pos.avg_entry_price) if pos.avg_entry_price else 0.0,
                "current_price": float(pos.current_price) if pos.current_price else 0.0,
                "unrealized_pl": float(pos.unrealized_pl) if pos.unrealized_pl else 0.0,
            }
        )

    # Get open orders (filter out terminal statuses)
    all_orders = client.get_orders()
    terminal_statuses = ["filled", "canceled", "expired", "rejected"]
    open_orders = [
        o
        for o in all_orders
        if o.status
        and (o.status.value if hasattr(o.status, "value") else str(o.status))
        not in terminal_statuses
    ]

    order_list = []
    for order in open_orders:
        order_list.append(
            {
                "id": str(order.id),
                "symbol": order.symbol,
                "side": order.side.value if hasattr(order.side, "value") else str(order.side),
                "qty": float(order.qty) if order.qty else 0,
                "filled_qty": float(order.filled_qty) if order.filled_qty else 0,
                "status": (
                    order.status.value if hasattr(order.status, "value") else str(order.status)
                ),
            }
        )

    return {
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "account": {
            "equity": float(account.equity),
            "cash": float(account.cash),
            "buying_power": float(account.buying_power),
        },
        "positions": position_list,
        "position_count": len(position_list),
        "open_orders": order_list,
        "open_order_count": len(order_list),
    }


def main():
    """Main entry point."""
    try:
        status = get_trading_status()
        print(json.dumps(status, indent=2))
        return 0
    except Exception as e:
        error_output = {
            "error": str(e),
            "timestamp": datetime.now(timezone.utc).isoformat(),
        }
        print(json.dumps(error_output, indent=2))
        return 1


if __name__ == "__main__":
    sys.exit(main())
