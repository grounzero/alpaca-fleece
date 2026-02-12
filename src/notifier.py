"""Alert notifier - send critical alerts via messaging (Tier 1)."""

import asyncio
import logging
import random
from typing import Optional

logger = logging.getLogger(__name__)


class AlertNotifier:
    """Send critical alerts to configured channels."""

    def __init__(self, alert_channel: Optional[str] = None, alert_target: Optional[str] = None):
        """Initialise alert notifier.

        Args:
            alert_channel: Channel type ('whatsapp', 'slack', 'email', None for logs only)
            alert_target: Target identifier (phone number, email, webhook URL, etc.)
        """
        self.alert_channel = alert_channel
        self.alert_target = alert_target
        self.enabled = alert_channel is not None and alert_target is not None
        # Retry/backoff configuration for transient failures
        self.retries = 3
        self.backoff = 1.0

    def _send_alert_sync(self, title: str, message: str, severity: str = "ERROR") -> bool:
        """Send alert to configured channel (Tier 1).

        Args:
            title: Alert title
            message: Alert message
            severity: Severity level (ERROR, WARNING, CRITICAL)

        Returns:
            True if sent, False if failed or disabled
        """
        if not self.enabled:
            # Fallback to logging
            logger.warning(f"[{severity}] {title}: {message}")
            return False

        # Single synchronous send attempt. Any retry/backoff is handled
        # by the async wrapper so this function stays fast and blocking-only.
        try:
            if self.alert_channel == "whatsapp":
                return self._send_whatsapp_alert(title, message, severity)
            elif self.alert_channel == "slack":
                return self._send_slack_alert(title, message, severity)
            elif self.alert_channel == "email":
                return self._send_email_alert(title, message, severity)
            else:
                logger.warning(f"Unknown alert channel: {self.alert_channel}")
                return False
        except Exception as e:
            logger.warning("Exception during sync alert send: %s", e)
            return False

    async def send_alert_async(self, title: str, message: str, severity: str = "ERROR") -> bool:
        """Async wrapper that performs retries with non-blocking backoff.

        The synchronous `_send_alert_sync` performs a single blocking send
        attempt; this async wrapper orchestrates retries and uses
        `asyncio.sleep` between attempts so the event loop is not blocked.
        """
        loop = asyncio.get_running_loop()

        last_exc: Exception | None = None
        for attempt in range(self.retries):
            try:
                ok = await loop.run_in_executor(
                    None, self._send_alert_sync, title, message, severity
                )
                if ok:
                    return True
                logger.warning(
                    "Alert send returned False (attempt %d/%d) for %s",
                    attempt + 1,
                    self.retries,
                    self.alert_channel,
                )
            except asyncio.CancelledError:
                # Allow cancellation to propagate
                raise
            except Exception as e:
                last_exc = e
                logger.warning(
                    "Exception sending alert async (attempt %d/%d): %s",
                    attempt + 1,
                    self.retries,
                    e,
                )

            # Backoff before next attempt unless this was the last attempt
            if attempt < self.retries - 1:
                delay = self.backoff * (2**attempt)
                # Add jitter
                delay = delay * (0.5 + random.random() * 0.5)
                try:
                    await asyncio.sleep(delay)
                except asyncio.CancelledError:
                    raise

        if last_exc:
            logger.error(f"Failed to send alert after {self.retries} attempts: {last_exc}")
        else:
            logger.error(f"Failed to send alert after {self.retries} attempts (no exception)")
        return False

    def send_alert(self, title: str, message: str, severity: str = "ERROR") -> bool:
        """Backward-compatible synchronous API expected by tests and callers."""
        last_exc: Exception | None = None
        for attempt in range(self.retries):
            try:
                ok = self._send_alert_sync(title, message, severity)
                if ok:
                    return True
                logger.warning(
                    "Alert send returned False (attempt %d/%d) for %s",
                    attempt + 1,
                    self.retries,
                    self.alert_channel,
                )
            except Exception as e:
                last_exc = e
                logger.warning(
                    "Exception sending alert (attempt %d/%d): %s",
                    attempt + 1,
                    self.retries,
                    e,
                )

            if attempt < self.retries - 1:
                try:
                    import time

                    delay = self.backoff * (2**attempt)
                    # jitter
                    delay = delay * (0.5 + random.random() * 0.5)
                    time.sleep(delay)
                except Exception:
                    logger.debug("Sleep interrupted during alert backoff")

        if last_exc:
            logger.error(f"Failed to send alert after {self.retries} attempts: {last_exc}")
        else:
            logger.error(f"Failed to send alert after {self.retries} attempts (no exception)")
        return False

    def _send_whatsapp_alert(self, title: str, message: str, severity: str) -> bool:
        """Send alert via WhatsApp (Tier 1).

        Uses OpenClaw message tool (future integration).
        """
        # Format message
        emoji = "🚨" if severity == "CRITICAL" else "⚠️" if severity == "WARNING" else "❌"
        formatted = f"{emoji} **{severity}** - {title}\n\n{message}"

        logger.info(f"WhatsApp alert to {self.alert_target}: {title}")
        logger.info(f"  Message: {formatted}")

        # TODO: Integrate with OpenClaw message tool
        # from message import send_alert
        # return send_alert(to=self.alert_target, message=formatted, channel="whatsapp")

        return True  # Placeholder

    def _send_slack_alert(self, title: str, message: str, severity: str) -> bool:
        """Send alert via Slack webhook (Tier 1)."""
        import json
        import urllib.request

        color_map = {
            "CRITICAL": "#FF0000",
            "ERROR": "#FF6600",
            "WARNING": "#FFCC00",
        }

        payload = {
            "attachments": [
                {
                    "color": color_map.get(severity, "#FF0000"),
                    "title": title,
                    "text": message,
                    "footer": "Alpaca Trading Bot",
                    "ts": int(__import__("time").time()),
                }
            ]
        }

        try:
            if self.alert_target is None:
                logger.error("Slack alert target is not configured")
                return False
            data = json.dumps(payload).encode()
            req = urllib.request.Request(
                self.alert_target,
                data=data,
                headers={"Content-Type": "application/json"},
            )
            urllib.request.urlopen(req, timeout=5)
            logger.info(f"Slack alert sent: {title}")
            return True
        except Exception as e:
            logger.error(f"Failed to send Slack alert: {e}")
            return False

    def _send_email_alert(self, title: str, message: str, severity: str) -> bool:
        """Send alert via email (Tier 1).

        SMTP credentials are NOT logged even on exception (security).
        """
        import smtplib
        from email.mime.multipart import MIMEMultipart
        from email.mime.text import MIMEText

        try:
            # Use environment variables for SMTP config
            import os

            smtp_host = os.getenv("SMTP_HOST", "localhost")
            smtp_port = int(os.getenv("SMTP_PORT", 587))
            smtp_user = os.getenv("SMTP_USER")
            smtp_pass = os.getenv("SMTP_PASSWORD")

            msg = MIMEMultipart()
            msg["From"] = smtp_user or "trading-bot@example.com"
            msg["To"] = self.alert_target or ""
            msg["Subject"] = f"[{severity}] {title}"

            msg.attach(MIMEText(message, "plain"))

            with smtplib.SMTP(smtp_host, smtp_port) as server:
                if smtp_user and smtp_pass:
                    server.starttls()
                    server.login(smtp_user, smtp_pass)
                server.send_message(msg)

            logger.info(f"Email alert sent to {self.alert_target}: {title}")
            return True

        except smtplib.SMTPAuthenticationError:
            # Sanitize: don't log credentials
            logger.error(
                f"SMTP authentication failed for {smtp_host}:{smtp_port}",
                extra={"error_type": "SMTPAuthenticationError"},
            )
            return False
        except smtplib.SMTPException as e:
            # SMTP-specific errors (don't expose credentials)
            logger.error(
                f"SMTP error sending email alert: {type(e).__name__}",
                extra={"error_type": type(e).__name__, "smtp_host": smtp_host},
            )
            return False
        except Exception as e:
            # Generic error handling (credentials already stripped)
            logger.error(
                f"Failed to send email alert: {type(e).__name__}",
                extra={"error_type": type(e).__name__},
            )
            return False

    def alert_circuit_breaker_tripped(self, failure_count: int) -> bool:
        """Alert: Circuit breaker tripped (Tier 1)."""
        return self._send_alert_sync(
            title="⚡ CIRCUIT BREAKER TRIPPED",
            message=f"Order submission failures: {failure_count}/5. Bot halted.",
            severity="CRITICAL",
        )

    async def alert_circuit_breaker_tripped_async(self, failure_count: int) -> bool:
        return await self.send_alert_async(
            title="⚡ CIRCUIT BREAKER TRIPPED",
            message=f"Order submission failures: {failure_count}/5. Bot halted.",
            severity="CRITICAL",
        )

    def alert_daily_loss_limit_exceeded(self, daily_pnl: float, limit: float) -> bool:
        """Alert: Daily loss limit exceeded (Tier 1)."""
        return self._send_alert_sync(
            title="💰 DAILY LOSS LIMIT EXCEEDED",
            message=f"Daily P&L: ${daily_pnl:.2f} (limit: ${limit:.2f}). Trading halted.",
            severity="CRITICAL",
        )

    async def alert_daily_loss_limit_exceeded_async(self, daily_pnl: float, limit: float) -> bool:
        return await self.send_alert_async(
            title="💰 DAILY LOSS LIMIT EXCEEDED",
            message=f"Daily P&L: ${daily_pnl:.2f} (limit: ${limit:.2f}). Trading halted.",
            severity="CRITICAL",
        )

    def alert_position_limit_exceeded(self, current: int, limit: int) -> bool:
        """Alert: Position limit exceeded (Tier 1)."""
        return self._send_alert_sync(
            title="📍 POSITION LIMIT EXCEEDED",
            message=f"Concurrent positions: {current}/{limit}. New trades blocked.",
            severity="WARNING",
        )

    async def alert_position_limit_exceeded_async(self, current: int, limit: int) -> bool:
        return await self.send_alert_async(
            title="📍 POSITION LIMIT EXCEEDED",
            message=f"Concurrent positions: {current}/{limit}. New trades blocked.",
            severity="WARNING",
        )

    def alert_order_submission_failed(self, symbol: str, error: str) -> bool:
        """Alert: Order submission failed (Tier 1)."""
        return self._send_alert_sync(
            title="❌ ORDER SUBMISSION FAILED",
            message=f"Symbol: {symbol}\nError: {error}",
            severity="ERROR",
        )

    async def alert_order_submission_failed_async(self, symbol: str, error: str) -> bool:
        return await self.send_alert_async(
            title="❌ ORDER SUBMISSION FAILED",
            message=f"Symbol: {symbol}\nError: {error}",
            severity="ERROR",
        )
