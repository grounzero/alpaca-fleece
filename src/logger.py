"""Logging configuration - JSON to file, human-readable to console."""

import json
import logging
import re
import uuid
from datetime import datetime, timezone
from logging.handlers import RotatingFileHandler
from pathlib import Path


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

    # Remove existing handlers to prevent duplicates
    for handler in logger.handlers[:]:
        logger.removeHandler(handler)

    # Validate log level
    log_level = log_level.upper()
    level = getattr(logging, log_level, logging.INFO)
    logger.setLevel(level)

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

    # Add redaction filter to remove sensitive values from logs
    class RedactingFilter(logging.Filter):
        RE = re.compile(r"(?i)(secret|token|password)([:=])\S+")

        def filter(self, record: logging.LogRecord) -> bool:
            try:
                text = record.getMessage()
            except Exception:
                text = str(record.msg)

            redacted = self.RE.sub(r"\1\2[REDACTED]", text)
            # Replace the record message and clear args to avoid reformatting
            record.msg = redacted
            record.args = None
            return True

    redactor = RedactingFilter()
    logger.addFilter(redactor)
    file_handler.addFilter(redactor)

    # Console handler (human-readable)
    console_handler = logging.StreamHandler()
    console_formatter = logging.Formatter("%(asctime)s [%(levelname)s] %(name)s: %(message)s")
    console_handler.setFormatter(console_formatter)
    console_handler.addFilter(redactor)
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
