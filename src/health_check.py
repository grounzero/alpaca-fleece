"""Health check endpoint for monitoring (Tier 1)."""

import json
import logging
from datetime import datetime, timezone
from typing import Dict, Any

logger = logging.getLogger(__name__)


class HealthCheck:
    """Health check status and monitoring (Tier 1)."""
    
    def __init__(self):
        """Initialise health check."""
        self.start_time = datetime.now(timezone.utc)
        self.last_trade_time = None
        self.last_signal_time = None
        self.error_count = 0
        self.is_healthy = True
        self.status_message = "OK"
    
    def record_trade(self) -> None:
        """Record that a trade was executed."""
        self.last_trade_time = datetime.now(timezone.utc)
    
    def record_signal(self) -> None:
        """Record that a signal was generated."""
        self.last_signal_time = datetime.now(timezone.utc)
    
    def record_error(self, error: str) -> None:
        """Record an error (Tier 1).
        
        Args:
            error: Error message
        """
        self.error_count += 1
        self.status_message = error
        
        if self.error_count > 5:
            self.is_healthy = False
            logger.error(f"Health check: {self.error_count} errors - marking unhealthy")
    
    def clear_errors(self) -> None:
        """Clear error count on recovery (Tier 1)."""
        self.error_count = 0
        self.is_healthy = True
        self.status_message = "OK"
    
    def get_status(self) -> Dict[str, Any]:
        """Get current health status (Tier 1).
        
        Returns:
            Dict with health status
        """
        uptime_seconds = (datetime.now(timezone.utc) - self.start_time).total_seconds()
        
        return {
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "status": "HEALTHY" if self.is_healthy else "UNHEALTHY",
            "message": self.status_message,
            "uptime_seconds": int(uptime_seconds),
            "uptime_hours": round(uptime_seconds / 3600, 1),
            "error_count": self.error_count,
            "last_trade": self.last_trade_time.isoformat() if self.last_trade_time else None,
            "last_signal": self.last_signal_time.isoformat() if self.last_signal_time else None,
        }
    
    def to_json(self) -> str:
        """Get health status as JSON (Tier 1).
        
        Returns:
            JSON string with status
        """
        return json.dumps(self.get_status(), indent=2)


# Global health check instance
_health_check = None


def initialise_health_check() -> HealthCheck:
    """Initialise global health check (Tier 1)."""
    global _health_check
    _health_check = HealthCheck()
    return _health_check


def get_health_check() -> HealthCheck:
    """Get global health check instance (Tier 1)."""
    global _health_check
    if _health_check is None:
        _health_check = HealthCheck()
    return _health_check
