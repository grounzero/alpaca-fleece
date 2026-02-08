"""Shared utility functions."""

from typing import Iterator, Sequence, TypeVar

T = TypeVar("T")


def batch_iter(items: Sequence[T], batch_size: int) -> Iterator[list[T]]:
    """Yield batches of items.

    Args:
        items: Sequence to batch
        batch_size: Size of each batch

    Yields:
        Lists of batch_size items (last batch may be smaller)

    Example:
        >>> list(batch_iter([1, 2, 3, 4, 5], 2))
        [[1, 2], [3, 4], [5]]
    """
    if batch_size < 0:
        raise ValueError("batch_size must be positive")
    if batch_size == 0:
        return
    for i in range(0, len(items), batch_size):
        yield list(items[i : i + batch_size])


def parse_optional_float(value: object) -> float | None:
    """Coerce a DB or metadata value to float if possible, otherwise return None.

    This helper lives in `src.utils` so multiple modules can import a stable,
    public function (avoids importing module-private helpers across modules).
    """
    if value is None:
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None
