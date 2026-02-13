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
    CANCELLED = "cancelled"  # Alpaca: canceled, pending_cancel
    EXPIRED = "expired"  # Alpaca: expired
    REJECTED = "rejected"  # Alpaca: rejected

    @classmethod
    def from_alpaca(cls, alpaca_status: str) -> "OrderState":
        """Convert Alpaca API status string to OrderState."""
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
        return mapping.get(alpaca_status.lower(), cls.PENDING)

    @property
    def is_terminal(self) -> bool:
        """Return True if order is in a terminal state."""
        return self in {
            OrderState.FILLED,
            OrderState.CANCELLED,
            OrderState.EXPIRED,
            OrderState.REJECTED,
        }

    @property
    def has_fill_potential(self) -> bool:
        """Return True if order may still receive fills."""
        return self in {
            OrderState.PENDING,
            OrderState.SUBMITTED,
            OrderState.PENDING_CANCEL,
            OrderState.PARTIAL,
        }
