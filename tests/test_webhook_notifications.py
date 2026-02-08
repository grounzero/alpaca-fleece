"""Tests for webhook notifications on critical events."""

import pytest
from datetime import datetime, timezone
from unittest.mock import MagicMock, patch

from src.notifier import AlertNotifier
from src.event_bus import ExitSignalEvent


class TestAlertNotifier:
    """Test AlertNotifier critical event methods."""

    def test_alert_circuit_breaker_tripped(self):
        """Test circuit breaker alert is sent with correct severity."""
        notifier = AlertNotifier(alert_channel="slack", alert_target="https://hooks.slack.com/test")
        
        with patch.object(notifier, '_send_slack_alert', return_value=True) as mock_send:
            result = notifier.alert_circuit_breaker_tripped(failure_count=5)
            
            assert result is True
            mock_send.assert_called_once()
            call_args = mock_send.call_args
            assert "CIRCUIT BREAKER TRIPPED" in call_args[0][0]  # title
            assert "5/5" in call_args[0][1]  # message contains failure count
            assert call_args[0][2] == "CRITICAL"  # severity

    def test_alert_daily_loss_limit_exceeded(self):
        """Test daily loss alert is sent with correct P&L values."""
        notifier = AlertNotifier(alert_channel="slack", alert_target="https://hooks.slack.com/test")
        
        with patch.object(notifier, '_send_slack_alert', return_value=True) as mock_send:
            result = notifier.alert_daily_loss_limit_exceeded(daily_pnl=-550.00, limit=500.00)
            
            assert result is True
            mock_send.assert_called_once()
            call_args = mock_send.call_args
            assert "DAILY LOSS LIMIT EXCEEDED" in call_args[0][0]  # title
            assert "$-550.00" in call_args[0][1]  # message contains daily P&L
            assert "$500.00" in call_args[0][1]  # message contains limit
            assert call_args[0][2] == "CRITICAL"  # severity

    def test_alert_kill_switch_activated(self):
        """Test kill switch alert is sent with CRITICAL severity."""
        notifier = AlertNotifier(alert_channel="slack", alert_target="https://hooks.slack.com/test")
        
        with patch.object(notifier, '_send_slack_alert', return_value=True) as mock_send:
            result = notifier.alert_kill_switch_activated()
            
            assert result is True
            mock_send.assert_called_once()
            call_args = mock_send.call_args
            assert "KILL SWITCH ACTIVATED" in call_args[0][0]  # title
            assert "Trading has been halted" in call_args[0][1]  # message
            assert call_args[0][2] == "CRITICAL"  # severity

    def test_send_alert_exit_triggered_profit(self):
        """Test exit alert for profitable exit."""
        notifier = AlertNotifier(alert_channel="slack", alert_target="https://hooks.slack.com/test")
        
        with patch.object(notifier, '_send_slack_alert', return_value=True) as mock_send:
            result = notifier.send_alert(
                title="Exit: AAPL (profit_target)",
                message="P&L: 2.5% ($125.50)",
                severity="INFO",
            )
            
            assert result is True
            mock_send.assert_called_once()
            call_args = mock_send.call_args
            assert "Exit: AAPL" in call_args[0][0]
            assert "profit_target" in call_args[0][0]
            assert call_args[0][2] == "INFO"

    def test_send_alert_exit_triggered_loss(self):
        """Test exit alert for loss exit (WARNING severity)."""
        notifier = AlertNotifier(alert_channel="slack", alert_target="https://hooks.slack.com/test")
        
        with patch.object(notifier, '_send_slack_alert', return_value=True) as mock_send:
            result = notifier.send_alert(
                title="Exit: TSLA (stop_loss)",
                message="P&L: -1.0% (-$50.25)",
                severity="WARNING",
            )
            
            assert result is True
            mock_send.assert_called_once()
            call_args = mock_send.call_args
            assert "Exit: TSLA" in call_args[0][0]
            assert "stop_loss" in call_args[0][0]
            assert call_args[0][2] == "WARNING"

    def test_notifier_disabled_logs_only(self):
        """Test that disabled notifier falls back to logging."""
        notifier = AlertNotifier(alert_channel=None, alert_target=None)
        
        with patch('src.notifier.logger') as mock_logger:
            result = notifier.alert_circuit_breaker_tripped(failure_count=5)
            
            assert result is False  # Returns False when disabled
            mock_logger.warning.assert_called()


class TestOrchestratorAlerts:
    """Test orchestrator sends alerts at critical points."""

    @pytest.fixture
    def mock_orchestrator(self):
        """Create a mock orchestrator with notifier."""
        from orchestrator import Orchestrator
        
        orch = Orchestrator()
        orch.notifier = MagicMock(spec=AlertNotifier)
        orch.notifier.enabled = True
        orch.state_store = MagicMock()
        orch.broker = MagicMock()
        return orch

    @pytest.mark.asyncio
    async def test_send_critical_alert_circuit_breaker(self, mock_orchestrator):
        """Test circuit breaker alert is sent correctly."""
        mock_orchestrator.notifier.alert_circuit_breaker_tripped.return_value = True
        
        result = await mock_orchestrator.send_critical_alert(
            "circuit_breaker_tripped",
            {"failure_count": 5}
        )
        
        assert result is True
        mock_orchestrator.notifier.alert_circuit_breaker_tripped.assert_called_once_with(5)

    @pytest.mark.asyncio
    async def test_send_critical_alert_daily_loss(self, mock_orchestrator):
        """Test daily loss alert is sent correctly."""
        mock_orchestrator.notifier.alert_daily_loss_limit_exceeded.return_value = True
        
        result = await mock_orchestrator.send_critical_alert(
            "daily_loss_exceeded",
            {"daily_pnl": -550.00, "limit": 500.00}
        )
        
        assert result is True
        mock_orchestrator.notifier.alert_daily_loss_limit_exceeded.assert_called_once_with(
            -550.00, 500.00
        )

    @pytest.mark.asyncio
    async def test_send_critical_alert_exit_triggered(self, mock_orchestrator):
        """Test exit triggered alert is sent correctly."""
        mock_orchestrator.notifier.send_alert.return_value = True
        
        result = await mock_orchestrator.send_critical_alert(
            "exit_triggered",
            {
                "symbol": "AAPL",
                "reason": "stop_loss",
                "pnl_pct": -0.01,
                "pnl_amount": -50.25,
            }
        )
        
        assert result is True
        mock_orchestrator.notifier.send_alert.assert_called_once()
        call_kwargs = mock_orchestrator.notifier.send_alert.call_args[1]
        assert "AAPL" in call_kwargs["title"]
        assert "stop_loss" in call_kwargs["title"]
        assert "-1.0%" in call_kwargs["message"]
        assert "$-50.25" in call_kwargs["message"]
        assert call_kwargs["severity"] == "WARNING"

    @pytest.mark.asyncio
    async def test_send_critical_alert_exit_profit(self, mock_orchestrator):
        """Test exit triggered alert shows INFO for profit."""
        mock_orchestrator.notifier.send_alert.return_value = True
        
        result = await mock_orchestrator.send_critical_alert(
            "exit_triggered",
            {
                "symbol": "TSLA",
                "reason": "profit_target",
                "pnl_pct": 0.025,
                "pnl_amount": 125.50,
            }
        )
        
        assert result is True
        call_kwargs = mock_orchestrator.notifier.send_alert.call_args[1]
        assert call_kwargs["severity"] == "INFO"

    @pytest.mark.asyncio
    async def test_send_critical_alert_kill_switch(self, mock_orchestrator):
        """Test kill switch alert is sent correctly."""
        mock_orchestrator.notifier.alert_kill_switch_activated.return_value = True
        
        result = await mock_orchestrator.send_critical_alert(
            "kill_switch_activated",
            {}
        )
        
        assert result is True
        mock_orchestrator.notifier.alert_kill_switch_activated.assert_called_once()

    @pytest.mark.asyncio
    async def test_send_critical_alert_unknown_type(self, mock_orchestrator):
        """Test unknown alert type logs warning."""
        with patch('orchestrator.logger') as mock_logger:
            result = await mock_orchestrator.send_critical_alert(
                "unknown_event",
                {}
            )
            
            assert result is False
            mock_logger.warning.assert_called_once()

    @pytest.mark.asyncio
    async def test_send_critical_alert_no_notifier(self, mock_orchestrator):
        """Test alert handling when notifier is not initialized."""
        mock_orchestrator.notifier = None
        
        with patch('orchestrator.logger') as mock_logger:
            result = await mock_orchestrator.send_critical_alert(
                "circuit_breaker_tripped",
                {"failure_count": 5}
            )
            
            assert result is False
            mock_logger.warning.assert_called_once()

    @pytest.mark.asyncio
    async def test_send_critical_alert_exception_handling(self, mock_orchestrator):
        """Test alert method handles notifier exceptions gracefully."""
        mock_orchestrator.notifier.alert_circuit_breaker_tripped.side_effect = Exception("Test error")
        
        with patch('orchestrator.logger') as mock_logger:
            result = await mock_orchestrator.send_critical_alert(
                "circuit_breaker_tripped",
                {"failure_count": 5}
            )
            
            assert result is False
            mock_logger.error.assert_called_once()


class TestIntegrationEvents:
    """Test that critical events trigger alerts in the event flow."""

    @pytest.mark.asyncio
    async def test_exit_signal_event_triggers_alert(self):
        """Test that ExitSignalEvent processing sends alert."""
        from orchestrator import Orchestrator
        
        # Create mock orchestrator
        orch = Orchestrator()
        orch.notifier = MagicMock(spec=AlertNotifier)
        orch.notifier.enabled = True
        orch.notifier.send_alert.return_value = True
        orch.state_store = MagicMock()
        orch.broker = MagicMock()
        orch.risk_manager = MagicMock()
        orch.order_manager = MagicMock()
        orch.position_tracker = MagicMock()
        orch.event_bus = MagicMock()
        orch.trading_config = {"risk": {}}
        
        # Create exit signal event
        exit_event = ExitSignalEvent(
            symbol="AAPL",
            side="sell",
            qty=10.0,
            reason="stop_loss",
            entry_price=150.0,
            current_price=148.5,
            pnl_pct=-0.01,
            pnl_amount=-15.0,
            timestamp=datetime.now(timezone.utc),
        )
        
        # Mock risk manager to allow exit
        orch.risk_manager.check_exit_order.return_value = True
        orch.order_manager.submit_order.return_value = True
        orch.position_tracker.stop_tracking.return_value = None
        
        # Process the exit signal (simulating what _event_processor does)
        await orch.send_critical_alert(
            "exit_triggered",
            {
                "symbol": exit_event.symbol,
                "reason": exit_event.reason,
                "pnl_pct": exit_event.pnl_pct,
                "pnl_amount": exit_event.pnl_amount,
            },
        )
        
        # Verify alert was sent
        orch.notifier.send_alert.assert_called_once()
        call_kwargs = orch.notifier.send_alert.call_args[1]
        assert "AAPL" in call_kwargs["title"]
        assert "stop_loss" in call_kwargs["title"]
