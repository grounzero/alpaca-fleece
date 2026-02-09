import gzip
import logging
import os
from logging.handlers import RotatingFileHandler

from src.backup_manager import BackupManager
from src.logger import setup_logger


def test_rotate_logs_moves_files(tmp_path, caplog):
    log_dir = tmp_path / "logs"
    # create logger in temporary dir
    logger = setup_logger(log_level="DEBUG", log_dir=str(log_dir))

    # find the rotating file handler and reduce its maxBytes to force rotation
    rfh = None
    for h in logger.handlers:
        if isinstance(h, RotatingFileHandler):
            rfh = h
            break

    assert rfh is not None
    # Force rotation on small size
    rfh.maxBytes = 1
    rfh.backupCount = 3

    # Emit several logs to trigger rotation
    for _ in range(10):
        logger.info("x" * 100)

    # Give file handlers a chance to flush
    for h in logger.handlers:
        h.flush()

    files = list(log_dir.glob("alpaca_bot.log*"))
    # Expect at least the main log and one rotated file
    assert any(f.name.endswith(".1") or f.name.endswith(".gz") for f in files)


def test_backup_fails_gracefully(tmp_path, monkeypatch, caplog):
    # Create a fake DB file
    db_path = tmp_path / "test.db"
    db_path.write_bytes(b"hello")

    backup_dir = tmp_path / "backups"
    bm = BackupManager(db_path=str(db_path), backup_dir=str(backup_dir))

    # Simulate gzip.open raising an IOError to trigger graceful failure
    def fake_gzip_open(*args, **kwargs):
        raise OSError("simulated IO error")

    monkeypatch.setattr("gzip.open", fake_gzip_open)

    caplog.clear()
    caplog.set_level(logging.ERROR)

    ok = bm.backup_database(compress=True)

    assert ok is False
    # Ensure an error was logged and no exception propagated
    assert any(
        "Backup failed" in r.message or "Backup failed" in r.getMessage() for r in caplog.records
    )


def test_cleanup_old_backups_removes_files(tmp_path):
    backup_dir = tmp_path / "backups"
    backup_dir.mkdir()

    # Create an old backup file
    old_file = backup_dir / "trades_20000101.db"
    old_file.write_text("old")
    # Set mtime far in the past
    old_time = 946684800  # 2000-01-01
    os.utime(old_file, (old_time, old_time))

    bm = BackupManager(db_path=str(tmp_path / "does_not_matter.db"), backup_dir=str(backup_dir))

    # Cleanup with days=0 should delete files older than now
    bm._cleanup_old_backups(days=0)

    assert not old_file.exists()


def test_restore_database_restores_compressed_and_uncompressed(tmp_path):
    db_path = tmp_path / "main.db"
    db_path.write_bytes(b"original")

    backup_dir = tmp_path / "backups"
    backup_dir.mkdir()

    # Create uncompressed backup
    uncompressed = backup_dir / "trades_uncompressed.db"
    uncompressed.write_bytes(b"uncompressed")

    # Create compressed backup
    compressed = backup_dir / "trades_compressed.db.gz"
    with gzip.open(compressed, "wb") as f:
        f.write(b"compressed")

    bm = BackupManager(db_path=str(db_path), backup_dir=str(backup_dir))

    assert bm.restore_database(uncompressed.name) is True
    assert db_path.read_bytes() == b"uncompressed"

    # restore from compressed
    assert bm.restore_database(compressed.name) is True
    assert db_path.read_bytes() == b"compressed"
