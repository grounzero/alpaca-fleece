import pytest

from src.async_broker_adapter import AsyncBrokerAdapter, BrokerFatalError
from src.broker import BrokerError as SyncBrokerError


@pytest.mark.asyncio
async def test_run_sync_raises_fatal_on_value_error_and_metrics():
    class BadBroker:
        def get_account(self):
            raise ValueError("invalid parameter")

    b = BadBroker()
    adapter = AsyncBrokerAdapter(b, max_workers=1, enable_cache=False)

    with pytest.raises(BrokerFatalError):
        await adapter.get_account()

    # Metric should have recorded a fatal
    key = "broker_fatals_total{method=get_account}"
    assert adapter.metrics.get(key, 0) == 1
    await adapter.close()


@pytest.mark.asyncio
async def test_run_sync_raises_fatal_on_brokererror_auth_and_metrics():
    class AuthFailBroker:
        def get_positions(self):
            raise SyncBrokerError("Authentication failed: invalid credentials")

    b = AuthFailBroker()
    adapter = AsyncBrokerAdapter(b, max_workers=1, enable_cache=False)

    with pytest.raises(BrokerFatalError):
        await adapter.get_positions()

    key = "broker_fatals_total{method=get_positions}"
    assert adapter.metrics.get(key, 0) == 1
    await adapter.close()
