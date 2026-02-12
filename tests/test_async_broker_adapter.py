import asyncio
import time

import pytest

from src.async_broker_adapter import AsyncBrokerAdapter


class SlowBroker:
    """Stub broker that sleeps to simulate slow Alpaca responses."""

    def get_clock(self):
        time.sleep(2.5)
        return {"is_open": True}

    def get_account(self):
        time.sleep(2.5)
        return {"equity": 1000.0, "buying_power": 5000.0, "cash": 1000.0}

    def get_positions(self):
        time.sleep(2.5)
        return []

    def get_open_orders(self):
        time.sleep(2.5)
        return []

    def submit_order(self, *args, **kwargs):
        time.sleep(2.5)
        return {"id": "ord123", "client_order_id": kwargs.get("client_order_id", "c1")}

    def cancel_order(self, order_id: str):
        time.sleep(2.5)
        return None


@pytest.mark.asyncio
async def test_slow_broker_does_not_block_event_loop():
    slow = SlowBroker()
    adapter = AsyncBrokerAdapter(slow, max_workers=4)

    # Start a background heartbeat task to ensure event loop remains responsive
    keep_running = True

    async def heartbeat():
        cnt = 0
        while keep_running and cnt < 10:
            await asyncio.sleep(0.1)
            cnt += 1

    hb = asyncio.create_task(heartbeat())

    # Issue several concurrent broker calls
    start = time.time()
    tasks = [
        asyncio.create_task(adapter.get_clock()),
        asyncio.create_task(adapter.get_account()),
        asyncio.create_task(adapter.get_positions()),
        asyncio.create_task(
            adapter.submit_order(symbol="AAPL", side="buy", qty=1, client_order_id="c1")
        ),
    ]

    results = await asyncio.gather(*tasks)
    duration = time.time() - start

    # Ensure we received results and event loop remained alive (heartbeat finished)
    assert isinstance(results[0], dict)
    assert isinstance(results[1], dict)
    assert isinstance(results[2], list)
    assert isinstance(results[3], dict)

    # Duration should be bounded (not sum of sleeps) due to parallelism
    assert duration < 6.0

    keep_running = False
    await hb
