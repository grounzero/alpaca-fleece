"""Logging configuration - JSON to file, human-readable to console."""

import json
import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path
from datetime import datetime, timezone
import uuid


def setup_logger(log_level: str = "INFO", log_dir: str = "logs") -> logging.Logger:
    """Setup logger with JSON file + console output.

    Args:
        log_level: Log level (DEBUG, INFO, WARNING, ERROR)
        log_dir: Directory for log files

    Returns:
        Configured logger
    """
    Path(log_dir).mkdir(parents=True, exist_ok=True)

    logger = logging.getLogger("alpaca_bot")
    logger.setLevel(getattr(logging, log_level))

    # Run ID for tracking sessions
    run_id = str(uuid.uuid4())[:8]

    # File handler (JSON)
    file_handler = RotatingFileHandler(
        f"{log_dir}/alpaca_bot.log",
        maxBytes=10 * 1024 * 1024,  # 10MB
        backupCount=30,  # 30 days retention
    )
    file_formatter = JSONFormatter(run_id)
    file_handler.setFormatter(file_formatter)
    logger.addHandler(file_handler)

    # Console handler (human-readable)
    console_handler = logging.StreamHandler()
    console_formatter = logging.Formatter("%(asctime)s [%(levelname)s] %(name)s: %(message)s")
    console_handler.setFormatter(console_formatter)
    logger.addHandler(console_handler)

    return logger


class JSONFormatter(logging.Formatter):
    """JSON logging formatter."""

    def __init__(self, run_id: str) -> None:
        self.run_id = run_id

    def format(self, record: logging.LogRecord) -> str:
        """Format record as JSON."""
        log_obj = {
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "run_id": self.run_id,
            "level": record.levelname,
            "module": record.name,
            "message": record.getMessage(),
        }
        return json.dumps(log_obj)
