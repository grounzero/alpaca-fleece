"""Structured logging with JSON format and daily rotation."""
import json
import logging
import sys
import uuid
from datetime import datetime
from logging.handlers import TimedRotatingFileHandler
from pathlib import Path
from typing import Any, Dict


# Global run ID for this execution
RUN_ID = str(uuid.uuid4())[:8]


class JSONFormatter(logging.Formatter):
    """Format log records as JSON."""

    def format(self, record: logging.LogRecord) -> str:
        """Format the log record as JSON."""
        log_data: Dict[str, Any] = {
            "timestamp": datetime.utcnow().isoformat() + "Z",
            "run_id": RUN_ID,
            "level": record.levelname,
            "logger": record.name,
            "message": record.getMessage(),
        }

        # Add exception info if present
        if record.exc_info:
            log_data["exception"] = self.formatException(record.exc_info)

        # Add extra fields
        for key, value in record.__dict__.items():
            if key not in [
                "name", "msg", "args", "created", "filename", "funcName",
                "levelname", "levelno", "lineno", "module", "msecs",
                "message", "pathname", "process", "processName",
                "relativeCreated", "thread", "threadName", "exc_info",
                "exc_text", "stack_info"
            ]:
                log_data[key] = value

        return json.dumps(log_data)


class ConsoleFormatter(logging.Formatter):
    """Human-readable console formatter."""

    COLOURS = {
        "DEBUG": "\033[36m",      # Cyan
        "INFO": "\033[32m",       # Green
        "WARNING": "\033[33m",    # Yellow
        "ERROR": "\033[31m",      # Red
        "CRITICAL": "\033[35m",   # Magenta
    }
    RESET = "\033[0m"

    def format(self, record: logging.LogRecord) -> str:
        """Format the log record for console."""
        colour = self.COLOURS.get(record.levelname, self.RESET)
        timestamp = datetime.utcnow().strftime("%H:%M:%S")

        # Base message
        msg = f"{colour}[{timestamp}] {record.levelname:8s}{self.RESET} {record.getMessage()}"

        # Add exception if present
        if record.exc_info:
            msg += "\n" + self.formatException(record.exc_info)

        return msg


def setup_logger(name: str, log_level: str = "INFO", log_dir: Path = None) -> logging.Logger:
    """
    Set up logger with JSON file output and human-readable console output.

    Args:
        name: Logger name
        log_level: Logging level (DEBUG, INFO, WARNING, ERROR, CRITICAL)
        log_dir: Directory for log files (default: logs/)

    Returns:
        Configured logger instance
    """
    logger = logging.getLogger(name)
    logger.setLevel(getattr(logging, log_level.upper()))

    # Remove existing handlers
    logger.handlers.clear()

    # Console handler (human-readable)
    console_handler = logging.StreamHandler(sys.stdout)
    console_handler.setLevel(getattr(logging, log_level.upper()))
    console_handler.setFormatter(ConsoleFormatter())
    logger.addHandler(console_handler)

    # File handler (JSON, daily rotation)
    if log_dir is None:
        log_dir = Path(__file__).parent.parent / "logs"

    log_dir.mkdir(exist_ok=True)

    file_handler = TimedRotatingFileHandler(
        filename=log_dir / f"{name}.log",
        when="midnight",
        interval=1,
        backupCount=30,  # Keep 30 days
        encoding="utf-8",
    )
    file_handler.setLevel(getattr(logging, log_level.upper()))
    file_handler.setFormatter(JSONFormatter())
    logger.addHandler(file_handler)

    return logger


def get_logger(name: str) -> logging.Logger:
    """Get an existing logger by name."""
    return logging.getLogger(name)


class SensitiveDataFilter(logging.Filter):
    """Filter to prevent logging of sensitive data."""

    SENSITIVE_KEYS = [
        "api_key", "secret_key", "password", "token", "credential",
        "alpaca_api_key", "alpaca_secret_key",
    ]

    def filter(self, record: logging.LogRecord) -> bool:
        """Check if record contains sensitive data."""
        message = record.getMessage().lower()

        # Check for sensitive keys in message
        for key in self.SENSITIVE_KEYS:
            if key in message:
                # Check if it looks like it's exposing a value (contains =, :, or quotes)
                if any(char in message for char in ["=", ":", '"', "'"]):
                    record.msg = f"[REDACTED] Message contained sensitive key: {key}"
                    record.args = ()
                    return True

        return True


# Add sensitive data filter to root logger
logging.getLogger().addFilter(SensitiveDataFilter())
