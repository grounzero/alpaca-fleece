from src.rate_limiter import RateLimiter


def test_get_backoff_delay_exponential():
    rl = RateLimiter(base_delay=2.0, max_delay=50.0)

    # failures == 0 -> 0.0
    rl.failures = 0
    assert rl.get_backoff_delay() == 0.0

    # failures == 1 -> base_delay * 2^(1-1) == base_delay
    rl.failures = 1
    assert rl.get_backoff_delay() == 2.0

    # failures == 2 -> base_delay * 2^(2-1) == 4.0
    rl.failures = 2
    assert rl.get_backoff_delay() == 4.0

    # higher failures scale exponentially
    rl.failures = 6
    assert rl.get_backoff_delay() == min(2.0 * (2 ** (6 - 1)), 50.0)

    # capped by max_delay
    rl = RateLimiter(base_delay=10.0, max_delay=20.0)
    rl.failures = 5
    assert rl.get_backoff_delay() == 20.0


def test_is_ready_to_retry_with_time(monkeypatch):
    rl = RateLimiter(base_delay=2.0, max_delay=100.0)
    rl.failures = 2
    # set last failure time to 1000.0
    rl.last_failure_time = 1000.0

    # backoff delay for failures=2 is base_delay * 2^(1) = 4.0
    _ = rl.get_backoff_delay()

    # time just after failure -> elapsed < delay -> not ready
    monkeypatch.setattr("src.rate_limiter.time.time", lambda: 1002.0)
    assert rl.is_ready_to_retry() is False

    # time after enough elapsed -> ready
    monkeypatch.setattr("src.rate_limiter.time.time", lambda: 1005.0)
    assert rl.is_ready_to_retry() is True
