"""Main entry point - thin wrapper around orchestrator.

This module provides backward compatibility as the main entry point.
The actual implementation has been moved to orchestrator.py which provides
a phase-based architecture matching the agent contracts in agents/.
"""

import asyncio
import sys

from orchestrator import main as orchestrator_main


async def main():
    """Main entry point - delegates to orchestrator."""
    return await orchestrator_main()


if __name__ == "__main__":
    exit_code = asyncio.run(main())
    sys.exit(exit_code)
