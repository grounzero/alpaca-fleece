from enum import Enum


class OrderState(Enum):
    """Canonical order states for the trading system.

    Maps Alpaca API statuses to internal enum values.
    """

    # Non-terminal states (order is still open)
    PENDING = "pending"  # Alpaca: new, pending_new
    SUBMITTED = "submitted"  # Alpaca: submitted, accepted
    PENDING_CANCEL = "pending_cancel"  # Alpaca: pending_cancel (cancellation requested)
    PARTIAL = "partial"  # Alpaca: partially_filled

    # Terminal states (order is complete/cancelled)
    FILLED = "filled"  # Alpaca: filled
    CANCELLED = "cancelled"  # Alpaca: canceled (no fills)
    EXPIRED = "expired"  # Alpaca: expired (no fills)
    REJECTED = "rejected"  # Alpaca: rejected (no fills)

    # Partial terminal states (order terminal but with partial fills)
    CANCELLED_PARTIAL = "cancelled_partial"  # Alpaca: canceled after partial fills
    EXPIRED_PARTIAL = "expired_partial"  # Alpaca: expired after partial fills
    REJECTED_PARTIAL = "rejected_partial"  # Alpaca: rejected after partial fills

    @classmethod
    def from_alpaca(
        cls,
        alpaca_status: str,
        filled_qty: float | None = None,
        order_qty: float | None = None,
    ) -> "OrderState":
        """Convert Alpaca API status string to OrderState.

        Args:
            alpaca_status: Alpaca order status string
            filled_qty: Cumulative filled quantity (for partial terminal detection)
            order_qty: Total order quantity (for partial terminal detection)

        Returns:
            OrderState enum value, with partial terminal states when applicable
        """
        status_lower = alpaca_status.lower()

        # Partial terminal detection: if order is in a terminal state with partial fills
        if status_lower in ("canceled", "expired", "rejected"):
            if filled_qty is not None and order_qty is not None:
                if 0 < filled_qty < order_qty:
                    # Partial fills present - use partial terminal state
                    if status_lower == "canceled":
                        return cls.CANCELLED_PARTIAL
                    elif status_lower == "expired":
                        return cls.EXPIRED_PARTIAL
                    elif status_lower == "rejected":
                        return cls.REJECTED_PARTIAL

        # Standard mapping (including full terminals with 0 fills or complete fills)
        mapping = {
            "new": cls.PENDING,
            "pending_new": cls.PENDING,
            "submitted": cls.SUBMITTED,
            "accepted": cls.SUBMITTED,
            "partially_filled": cls.PARTIAL,
            "filled": cls.FILLED,
            "canceled": cls.CANCELLED,
            # "pending_cancel" indicates a cancellation request has been
            # submitted but the order may still receive fills until the
            # exchange acknowledges the cancel. Treat it as a non-terminal
            # state with fill potential.
            "pending_cancel": cls.PENDING_CANCEL,
            "expired": cls.EXPIRED,
            "rejected": cls.REJECTED,
        }
        return mapping.get(status_lower, cls.PENDING)

    @property
    def is_terminal(self) -> bool:
        """Return True if order is in a terminal state (including partial terminals)."""
        return self in {
            OrderState.FILLED,
            OrderState.CANCELLED,
            OrderState.EXPIRED,
            OrderState.REJECTED,
            OrderState.CANCELLED_PARTIAL,
            OrderState.EXPIRED_PARTIAL,
            OrderState.REJECTED_PARTIAL,
        }

    @property
    def has_fill_potential(self) -> bool:
        """Return True if order may still receive fills.

        All terminal states (including partial terminals) return False as they
        cannot receive additional fills once terminal.
        """
        return self in {
            OrderState.PENDING,
            OrderState.SUBMITTED,
            OrderState.PENDING_CANCEL,
            OrderState.PARTIAL,
        }
