"""Rate limiter with exponential backoff for API calls."""

import asyncio
import time


class RateLimiter:
    """Exponential backoff rate limiter.

    Protects against API rate limits by:
    1. Tracking failed attempts
    2. Applying exponential backoff (2^n)
    3. Auto-recovery when successful
    """

    def __init__(
        self,
        base_delay: float = 1.0,
        max_delay: float = 120.0,
        max_retries: int = 10,
    ) -> None:
        """Initialise rate limiter.

        Args:
            base_delay: Initial backoff delay (seconds)
            max_delay: Maximum backoff delay (seconds)
            max_retries: Maximum reconnection attempts
        """
        self.base_delay = base_delay
        self.max_delay = max_delay
        self.max_retries = max_retries

        self.failures = 0
        self.last_failure_time = 0
        self.is_limited = False

    def get_backoff_delay(self) -> float:
        """Calculate exponential backoff delay.

        Returns:
            Seconds to wait before next attempt
        """
        if self.failures == 0:
            return 0

        # Exponential: 2^n, capped at max_delay
        delay = min(self.base_delay * (2 ** (self.failures - 1)), self.max_delay)
        return delay

    def record_failure(self) -> None:
        """Record a failed attempt."""
        self.failures += 1
        self.last_failure_time = time.time()

        if self.failures >= self.max_retries:
            self.is_limited = True

    def record_success(self) -> None:
        """Record a successful attempt (reset)."""
        self.failures = 0
        self.is_limited = False

    async def wait_if_limited(self) -> None:
        """Wait if rate limited, respecting exponential backoff."""
        if self.failures == 0:
            return

        delay = self.get_backoff_delay()
        if delay > 0:
            await asyncio.sleep(delay)

    def is_ready_to_retry(self) -> bool:
        """Check if enough time has passed for retry."""
        if self.failures == 0:
            return True

        delay = self.get_backoff_delay()
        elapsed = time.time() - self.last_failure_time
        return elapsed >= delay

    def get_status(self) -> dict:
        """Get rate limiter status."""
        return {
            "failures": self.failures,
            "is_limited": self.is_limited,
            "backoff_delay": self.get_backoff_delay(),
            "is_ready_to_retry": self.is_ready_to_retry(),
            "last_failure_time": self.last_failure_time,
        }
