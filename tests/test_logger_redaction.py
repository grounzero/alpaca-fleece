import logging
from pathlib import Path

from src.logger import setup_logger


def test_logger_redacts_sensitive(tmp_path, caplog):
    log_dir = tmp_path / "logs"
    log_dir.mkdir()

    # Configure logger to write to temp dir
    logger = setup_logger(log_level="INFO", log_dir=str(log_dir))

    caplog.set_level(logging.INFO)

    # Emit a message containing sensitive tokens
    logger.info("user=alice secret=abc123 token=tok_987 password=hunter2")

    # Console output captured in caplog should contain redacted markers
    assert "[REDACTED]" in caplog.text
    assert "abc123" not in caplog.text

    # Also verify file output (JSON) is redacted
    log_file = Path(log_dir) / "alpaca_bot.log"
    # Ensure handlers flushed
    for h in logger.handlers:
        try:
            h.flush()
        except Exception:
            pass

    content = log_file.read_text()
    assert "[REDACTED]" in content
    assert "abc123" not in content
