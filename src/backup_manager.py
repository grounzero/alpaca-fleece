"""Database backup manager - daily SQLite backups (Tier 1)."""

import gzip
import logging
import shutil
from datetime import date, datetime, timezone
from pathlib import Path
from typing import Optional

logger = logging.getLogger(__name__)


class BackupManager:
    """Manage SQLite database backups (Tier 1)."""

    def __init__(self, db_path: str, backup_dir: str = "backups") -> None:
        """Initialise backup manager.

        Args:
            db_path: Path to SQLite database file
            backup_dir: Directory to store backups
        """
        self.db_path = db_path
        self.backup_dir = Path(backup_dir)
        self.backup_dir.mkdir(exist_ok=True)

        self.last_backup_date: Optional[date] = None

    def backup_database(self, compress: bool = True) -> bool:
        """Create a backup of the database (Tier 1).

        Args:
            compress: Compress backup with gzip

        Returns:
            True if successful, False if failed
        """
        try:
            db_path = Path(self.db_path)
            if not db_path.exists():
                logger.warning(f"Database not found: {self.db_path}")
                return False

            # Generate backup filename with timestamp
            now = datetime.now(timezone.utc)
            timestamp = now.strftime("%Y%m%d_%H%M%S")

            if compress:
                backup_filename = f"trades_{timestamp}.db.gz"
                backup_path = self.backup_dir / backup_filename

                # Compress to gzip
                with open(self.db_path, "rb") as f_in:
                    with gzip.open(backup_path, "wb") as f_out:
                        shutil.copyfileobj(f_in, f_out)
            else:
                backup_filename = f"trades_{timestamp}.db"
                backup_path = self.backup_dir / backup_filename

                # Copy database file
                shutil.copy2(self.db_path, backup_path)

            size_kb = backup_path.stat().st_size / 1024
            logger.info(f"Database backup created: {backup_filename} ({size_kb:.1f} KB)")

            self.last_backup_date = now.date()

            # Clean up old backups (keep last 30 days)
            self._cleanup_old_backups(days=30)

            return True

        except Exception as e:
            logger.error(f"Backup failed: {e}")
            return False

    def _cleanup_old_backups(self, days: int = 30) -> None:
        """Delete backups older than specified days (Tier 1).

        Args:
            days: Keep backups from last N days
        """
        try:
            cutoff = datetime.now(timezone.utc).timestamp() - (days * 86400)

            for backup_file in self.backup_dir.glob("trades_*.db*"):
                if backup_file.stat().st_mtime < cutoff:
                    backup_file.unlink()
                    logger.info(f"Deleted old backup: {backup_file.name}")

        except Exception as e:
            logger.error(f"Cleanup failed: {e}")

    def restore_database(self, backup_filename: str) -> bool:
        """Restore database from backup (Tier 1).

        Args:
            backup_filename: Name of backup file to restore

        Returns:
            True if successful, False if failed
        """
        try:
            backup_path = self.backup_dir / backup_filename

            if not backup_path.exists():
                logger.error(f"Backup not found: {backup_filename}")
                return False

            # Check if compressed
            if backup_filename.endswith(".gz"):
                # Decompress and restore
                with gzip.open(backup_path, "rb") as f_in:
                    with open(self.db_path, "wb") as f_out:
                        shutil.copyfileobj(f_in, f_out)
            else:
                # Direct copy
                shutil.copy2(backup_path, self.db_path)

            logger.info(f"Database restored from: {backup_filename}")
            return True

        except Exception as e:
            logger.error(f"Restore failed: {e}")
            return False

    def get_backup_list(self) -> list[dict[str, object]]:
        """Get list of available backups (Tier 1).

        Returns:
            List of backup filenames with sizes
        """
        backups: list[dict[str, object]] = []
        for backup_file in sorted(self.backup_dir.glob("trades_*.db*"), reverse=True):
            size_kb = backup_file.stat().st_size / 1024
            backups.append(
                {
                    "filename": backup_file.name,
                    "size_kb": size_kb,
                    "modified": datetime.fromtimestamp(backup_file.stat().st_mtime),
                }
            )
        return backups
