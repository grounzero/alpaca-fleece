"""Housekeeping - periodic maintenance tasks.

Responsibilities:
- Snapshot equity every 60s
- Reset daily counters at 09:30 ET
- Log periodic health status
"""

import asyncio
import logging
import sqlite3
from datetime import datetime, timezone

import pytz  # type: ignore[import-untyped]

from src.broker import Broker
from src.state_store import StateStore

logger = logging.getLogger(__name__)

ET = pytz.timezone("America/New_York")


class Housekeeping:
    """Periodic maintenance."""

    def __init__(self, broker: Broker, state_store: StateStore) -> None:
        """Initialise housekeeping.

        Args:
            broker: Broker client
            state_store: State store
        """
        self.broker = broker
        self.state_store = state_store
        self.running = False

    async def start(self) -> None:
        """Start housekeeping tasks."""
        logger.info("Housekeeping.start() entered")
        self.running = True

        # Run equity snapshot and daily reset tasks
        tasks = [
            asyncio.create_task(self._equity_snapshots(), name="equity_snapshots"),
            asyncio.create_task(self._daily_resets(), name="daily_resets"),
        ]

        try:
            logger.info("Housekeeping: waiting on internal gather...")
            # Use return_exceptions=True to prevent one crashing task from killing both
            results = await asyncio.gather(*tasks, return_exceptions=True)

            # CRITICAL: If we reach here, one of the housekeeping tasks exited
            logger.critical("CRITICAL: Housekeeping internal gather returned - a task exited!")

            # Check for exceptions and log them
            for i, result in enumerate(results):
                task_name = "_equity_snapshots" if i == 0 else "_daily_resets"
                if isinstance(result, Exception):
                    logger.error(f"Housekeeping task {task_name} failed: {result}", exc_info=result)
                else:
                    logger.critical(f"Housekeeping task {task_name} returned normally: {result}")

        except asyncio.CancelledError:
            logger.info("Housekeeping.start() cancelled")
            self.running = False
            raise  # Re-raise to properly handle shutdown

        logger.info("Housekeeping.start() exiting - this should NOT happen during normal operation")

    async def stop(self) -> None:
        """Stop housekeeping tasks."""
        self.running = False

    async def graceful_shutdown(self) -> None:
        """Graceful shutdown: cancel orders, close positions, save state (Tier 1).

        Called on SIGTERM/SIGINT to safely exit.
        """
        logger.info("Graceful shutdown initiated...")

        try:
            # Step 1: Cancel all open orders
            logger.info("Cancelling open orders...")
            try:
                orders = self.broker.get_open_orders()
                for order in orders:
                    order_id = order.get("id")
                    if order_id:
                        self.broker.cancel_order(order_id)
                        logger.info(f"Cancelled order: {order_id}")
            except (ConnectionError, TimeoutError) as e:
                logger.error(f"Failed to cancel orders: {e}")

            # Step 2: Close all open positions
            logger.info("Closing open positions...")
            try:
                positions = self.broker.get_positions()
                for position in positions:
                    symbol = position["symbol"]
                    qty = float(position["qty"])
                    side = "sell" if qty > 0 else "buy"
                    abs_qty = abs(qty)
                    client_order_id = f"close_{symbol}_{datetime.now(timezone.utc).isoformat()}"

                    logger.info(f"Closing position: {symbol} ({abs_qty} shares via {side})")
                    self.broker.submit_order(
                        symbol=symbol,
                        side=side,
                        qty=abs_qty,
                        client_order_id=client_order_id,
                        order_type="market",
                    )
            except (ConnectionError, TimeoutError) as e:
                logger.error(f"Failed to close positions: {e}")

            # Step 3: Take final equity snapshot
            logger.info("Recording final state...")
            try:
                account = self.broker.get_account()
                equity = account["equity"]

                import sqlite3

                conn = sqlite3.connect(self.state_store.db_path)
                cursor = conn.cursor()
                cursor.execute(
                    """INSERT INTO equity_curve (timestamp_utc, equity, daily_pnl)
                       VALUES (?, ?, ?)""",
                    (datetime.now(timezone.utc).isoformat(), equity, 0),
                )
                conn.commit()
                conn.close()

                logger.info(f"Final equity snapshot: ${equity:.2f}")
            except (sqlite3.Error, ConnectionError) as e:
                logger.error(f"Failed to record final state: {e}")

            logger.info("Graceful shutdown complete")

        except (ConnectionError, TimeoutError) as e:
            logger.error(f"Graceful shutdown error: {e}")

    async def _equity_snapshots(self) -> None:
        """Snapshot equity every 60s."""
        logger.info("Equity snapshots task started")
        try:
            iteration = 0
            while self.running:
                iteration += 1
                if iteration % 5 == 0:  # Log every 5 iterations (5 minutes)
                    logger.info(f"Equity snapshots: iteration {iteration}, running={self.running}")
                try:
                    account = self.broker.get_account()
                    equity = account["equity"]
                    daily_pnl = float(self.state_store.get_state("daily_pnl") or 0)

                    # Record to equity_curve table
                    import sqlite3

                    conn = sqlite3.connect(self.state_store.db_path)
                    cursor = conn.cursor()
                    cursor.execute(
                        """INSERT INTO equity_curve (timestamp_utc, equity, daily_pnl)
                           VALUES (?, ?, ?)""",
                        (datetime.now(timezone.utc).isoformat(), equity, daily_pnl),
                    )
                    conn.commit()
                    conn.close()

                    logger.debug(f"Equity snapshot: ${equity:.2f}")
                except asyncio.CancelledError:
                    raise  # Re-raise to handle graceful shutdown
                except (ConnectionError, TimeoutError, sqlite3.Error) as e:
                    logger.error(f"Equity snapshot failed: {e}")

                await asyncio.sleep(60)

            logger.critical("Equity snapshots: while loop exited - running became False!")

        except asyncio.CancelledError:
            logger.info("Equity snapshots task cancelled")
            raise
        except (ConnectionError, TimeoutError, sqlite3.Error) as e:
            logger.critical(f"Equity snapshots task crashed: {e}", exc_info=True)
            raise  # Propagate to be caught by gather
        finally:
            logger.info(f"Equity snapshots task exiting after {iteration} iterations")

    async def _daily_resets(self) -> None:
        """Reset daily counters at 09:30 ET."""
        logger.info("Daily resets task started")
        try:
            iteration = 0
            while self.running:
                iteration += 1
                if iteration % 10 == 0:  # Log every 10 iterations
                    logger.info(f"Daily resets: iteration {iteration}, running={self.running}")
                try:
                    # Check if it's 09:30 ET
                    now_et = datetime.now(ET)

                    # Only reset once per day
                    last_reset_date = self.state_store.get_state("daily_reset_date")
                    today = now_et.date().isoformat()

                    if now_et.hour == 9 and now_et.minute >= 30 and last_reset_date != today:

                        logger.info("Daily reset at 09:30 ET")
                        self.state_store.set_state("daily_trade_count", "0")
                        self.state_store.set_state("daily_pnl", "0")
                        self.state_store.set_state("daily_reset_date", today)

                except asyncio.CancelledError:
                    raise  # Re-raise to handle graceful shutdown
                except (sqlite3.Error, ValueError) as e:
                    logger.error(f"Daily reset failed: {e}")

                # Check every minute
                await asyncio.sleep(60)

            logger.critical("Daily resets: while loop exited - running became False!")

        except asyncio.CancelledError:
            logger.info("Daily resets task cancelled")
            raise
        except (sqlite3.Error, ValueError) as e:
            logger.critical(f"Daily resets task crashed: {e}", exc_info=True)
            raise  # Propagate to be caught by gather
        finally:
            logger.info(f"Daily resets task exiting after {iteration} iterations")
