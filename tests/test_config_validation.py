"""Tests for config validation."""

import pytest

from src.config import ConfigError, validate_exit_config


class TestValidateExitConfig:
    """Test cases for validate_exit_config function."""

    def test_valid_config_passes(self):
        """Test that valid exit config passes validation."""
        config = {
            "stop_loss_pct": 0.01,
            "profit_target_pct": 0.02,
            "trailing_stop_activation_pct": 0.01,
            "trailing_stop_trail_pct": 0.005,
        }
        # Should not raise any exception
        validate_exit_config(config)

    def test_default_values_pass(self):
        """Test that default values pass validation."""
        config = {}
        # Should use defaults and not raise
        validate_exit_config(config)

    def test_negative_stop_loss_raises_config_error(self):
        """Test that negative stop_loss_pct raises ConfigError."""
        config = {
            "stop_loss_pct": -0.01,
            "profit_target_pct": 0.02,
        }
        with pytest.raises(ConfigError, match="stop_loss_pct must be between 0 and 1"):
            validate_exit_config(config)

    def test_zero_stop_loss_raises_config_error(self):
        """Test that zero stop_loss_pct raises ConfigError."""
        config = {
            "stop_loss_pct": 0.0,
            "profit_target_pct": 0.02,
        }
        with pytest.raises(ConfigError, match="stop_loss_pct must be between 0 and 1"):
            validate_exit_config(config)

    def test_stop_loss_equal_to_one_raises_config_error(self):
        """Test that stop_loss_pct equal to 1.0 raises ConfigError."""
        config = {
            "stop_loss_pct": 1.0,
            "profit_target_pct": 0.02,
        }
        with pytest.raises(ConfigError, match="stop_loss_pct must be between 0 and 1"):
            validate_exit_config(config)

    def test_stop_loss_greater_than_one_raises_config_error(self):
        """Test that stop_loss_pct > 1.0 raises ConfigError."""
        config = {
            "stop_loss_pct": 1.5,
            "profit_target_pct": 0.02,
        }
        with pytest.raises(ConfigError, match="stop_loss_pct must be between 0 and 1"):
            validate_exit_config(config)

    def test_profit_target_greater_than_one_raises_config_error(self):
        """Test that profit_target_pct > 1.0 raises ConfigError."""
        config = {
            "stop_loss_pct": 0.01,
            "profit_target_pct": 1.5,
        }
        with pytest.raises(ConfigError, match="profit_target_pct must be between 0 and 1"):
            validate_exit_config(config)

    def test_negative_profit_target_raises_config_error(self):
        """Test that negative profit_target_pct raises ConfigError."""
        config = {
            "stop_loss_pct": 0.01,
            "profit_target_pct": -0.02,
        }
        with pytest.raises(ConfigError, match="profit_target_pct must be between 0 and 1"):
            validate_exit_config(config)

    def test_trailing_trail_greater_than_stop_loss_raises_config_error(self):
        """Test that trailing_stop_trail_pct >= stop_loss_pct raises ConfigError."""
        config = {
            "stop_loss_pct": 0.01,
            "profit_target_pct": 0.02,
            "trailing_stop_trail_pct": 0.01,  # Equal to stop_loss
        }
        with pytest.raises(
            ConfigError, match="trailing_stop_trail_pct .* must be less than stop_loss_pct"
        ):
            validate_exit_config(config)

    def test_trailing_trail_equal_to_stop_loss_raises_config_error(self):
        """Test that trailing_stop_trail_pct == stop_loss_pct raises ConfigError."""
        config = {
            "stop_loss_pct": 0.01,
            "profit_target_pct": 0.02,
            "trailing_stop_trail_pct": 0.01,  # Equal to stop_loss
        }
        with pytest.raises(
            ConfigError, match="trailing_stop_trail_pct .* must be less than stop_loss_pct"
        ):
            validate_exit_config(config)

    def test_trailing_trail_less_than_stop_loss_passes(self):
        """Test that trailing_stop_trail_pct < stop_loss_pct passes."""
        config = {
            "stop_loss_pct": 0.02,
            "profit_target_pct": 0.04,
            "trailing_stop_trail_pct": 0.01,  # Less than stop_loss
        }
        # Should not raise
        validate_exit_config(config)

    def test_negative_trailing_activation_raises_config_error(self):
        """Test that negative trailing_stop_activation_pct raises ConfigError."""
        config = {
            "stop_loss_pct": 0.01,
            "profit_target_pct": 0.02,
            "trailing_stop_activation_pct": -0.01,
        }
        with pytest.raises(ConfigError, match="trailing_stop_activation_pct must be positive"):
            validate_exit_config(config)

    def test_zero_trailing_activation_raises_config_error(self):
        """Test that zero trailing_stop_activation_pct raises ConfigError."""
        config = {
            "stop_loss_pct": 0.01,
            "profit_target_pct": 0.02,
            "trailing_stop_activation_pct": 0.0,
        }
        with pytest.raises(ConfigError, match="trailing_stop_activation_pct must be positive"):
            validate_exit_config(config)

    def test_edge_case_valid_config_at_boundaries(self):
        """Test valid config at boundary values."""
        config = {
            "stop_loss_pct": 0.001,  # Very small but valid
            "profit_target_pct": 0.001,
            "trailing_stop_activation_pct": 0.001,
            "trailing_stop_trail_pct": 0.0005,  # Less than stop_loss
        }
        # Should not raise
        validate_exit_config(config)

    def test_edge_case_high_values(self):
        """Test valid config with high but valid values."""
        config = {
            "stop_loss_pct": 0.99,  # High but less than 1
            "profit_target_pct": 0.99,
            "trailing_stop_activation_pct": 0.1,
            "trailing_stop_trail_pct": 0.5,  # Less than stop_loss of 0.99
        }
        # Should not raise
        validate_exit_config(config)