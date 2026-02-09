#!/usr/bin/env python3
"""Analyse runtime signal logs and compute signal vs regime statistics.

Parses log lines emitted by `SMACrossover` (info lines) with the format:

  signal=BUY symbol=AAPL sma=(20, 50) confidence=0.900 regime=trending strength=0.800 atr=0.123 close=102.00

Usage:
  python tools/analyse_signals.py /path/to/orchestrator.log

Outputs a summary table with counts and percentages and writes optional CSV.
"""

from __future__ import annotations

import argparse
import re
from pathlib import Path

try:
    import pandas as pd
except Exception:  # pragma: no cover - optional runtime dependency
    print("This tool requires pandas and numpy. Install via: pip install pandas numpy")
    raise


PATTERN = re.compile(
    r"signal=(?P<signal>\w+)\s+symbol=(?P<symbol>\S+)\s+sma=(?P<sma>\([^)]+\))\s+"
    r"confidence=(?P<confidence>[0-9.]+)\s+regime=(?P<regime>\w+)\s+strength=(?P<strength>[0-9.]+)\s+"
    r"atr=(?P<atr>[^\s]+)\s+close=(?P<close>[0-9.]+)"
)


def parse_log(path: Path) -> pd.DataFrame:
    rows = []
    with path.open("r", encoding="utf-8", errors="ignore") as fh:
        for line in fh:
            m = PATTERN.search(line)
            if not m:
                continue
            d = m.groupdict()
            try:
                d["confidence"] = float(d["confidence"])
            except Exception:
                d["confidence"] = float("nan")
            try:
                d["strength"] = float(d["strength"])
            except Exception:
                d["strength"] = float("nan")
            # ATR may be 'None'
            if d["atr"] in ("None", "nan"):
                d["atr"] = None
            else:
                try:
                    d["atr"] = float(d["atr"])
                except Exception:
                    d["atr"] = None
            try:
                d["close"] = float(d["close"])
            except Exception:
                d["close"] = float("nan")

            rows.append(d)

    if not rows:
        return pd.DataFrame()

    df = pd.DataFrame(rows)
    # Clean up types
    df["sma"] = df["sma"].astype(str)
    df["signal"] = df["signal"].astype(str)
    df["symbol"] = df["symbol"].astype(str)
    return df


def summarize(df: pd.DataFrame) -> None:
    total = len(df)
    print(f"Total signals parsed: {total}\n")

    regimes = df["regime"].value_counts(dropna=False)
    print("Regime distribution (signals):")
    for r, c in regimes.items():
        pct = c / total * 100
        print(f"  {r}: {c} ({pct:.1f}%)")
    print()

    print("Per-SMA breakdown:")
    groups = df.groupby("sma")
    for sma, g in groups:
        print(f"  SMA {sma}: {len(g)} signals")
        rc = g["regime"].value_counts(normalize=True)
        for r, p in rc.items():
            print(f"    {r}: {p * 100:.1f}%")
        print(f"    avg_confidence: {g['confidence'].mean():.3f}")
        print()

    # Correlation between confidence and regime strength
    if df["strength"].notna().any():
        # Pearson correlation
        valid = df[["confidence", "strength"]].dropna()
        if len(valid) >= 2:
            corr = valid["confidence"].corr(valid["strength"])
            print(f"Correlation (confidence vs regime_strength): {corr:.3f}")
        else:
            print("Not enough valid points for correlation")


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="Analyze signal logs for regime correlation")
    p.add_argument("logfile", type=Path, help="Path to log file to analyze")
    p.add_argument("--csv", type=Path, help="Optional output CSV path")
    args = p.parse_args(argv)

    if not args.logfile.exists():
        print(f"Log file not found: {args.logfile}")
        return 2

    df = parse_log(args.logfile)
    if df.empty:
        print("No signal lines found in log file.")
        return 0

    summarize(df)

    if args.csv:
        df.to_csv(args.csv, index=False)
        print(f"Wrote parsed signals to {args.csv}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
