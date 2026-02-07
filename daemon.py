#!/usr/bin/env python3
"""
Daemon wrapper for Alpaca Trading Bot.

Uses the double-fork technique to properly daemonize the process:
1. First fork: Detach from parent terminal
2. Second fork: Prevent acquisition of controlling terminal
3. Change working directory, umask, and close file descriptors
4. Write PID file for management
"""

import os
import sys
import signal
import atexit
import argparse
import asyncio
from pathlib import Path

# Add the alpaca-bot directory to path (auto-detect from script location)
BOT_DIR = Path(__file__).parent.resolve()
sys.path.insert(0, str(BOT_DIR))

# PID file location
PID_FILE = BOT_DIR / "data" / "alpaca_bot.pid"
LOG_FILE = BOT_DIR / "logs" / "daemon.log"


def write_pid(pid):
    """Write PID to file."""
    PID_FILE.parent.mkdir(parents=True, exist_ok=True)
    with open(PID_FILE, 'w') as f:
        f.write(str(pid))


def read_pid():
    """Read PID from file."""
    try:
        with open(PID_FILE, 'r') as f:
            return int(f.read().strip())
    except (FileNotFoundError, ValueError):
        return None


def remove_pid():
    """Remove PID file."""
    try:
        PID_FILE.unlink()
    except FileNotFoundError:
        pass


def is_running(pid):
    """Check if process with PID is running."""
    try:
        os.kill(pid, 0)
        return True
    except (OSError, ProcessLookupError):
        return False


def daemonize():
    """
    Perform double-fork daemonization.
    
    This creates a truly detached daemon process that:
    - Has no controlling terminal
    - Is not affected by session termination
    - Runs in its own session and process group
    """
    # First fork
    pid = os.fork()
    if pid > 0:
        # Parent exits
        sys.exit(0)
    
    # Decouple from parent environment
    os.chdir(BOT_DIR)
    os.umask(0)
    os.setsid()  # Create new session, detach from terminal
    
    # Second fork to prevent acquiring controlling terminal
    pid = os.fork()
    if pid > 0:
        # First child exits
        sys.exit(0)
    
    # Grandchild continues as daemon
    
    # Redirect standard file descriptors to /dev/null or log file
    sys.stdout.flush()
    sys.stderr.flush()
    
    # Open log file for daemon messages
    LOG_FILE.parent.mkdir(parents=True, exist_ok=True)
    log_fd = os.open(LOG_FILE, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o644)
    
    # Redirect stdout and stderr to log file
    os.dup2(log_fd, 1)  # stdout
    os.dup2(log_fd, 2)  # stderr
    os.close(log_fd)
    
    # Redirect stdin from /dev/null
    devnull = os.open('/dev/null', os.O_RDONLY)
    os.dup2(devnull, 0)
    os.close(devnull)
    
    # Write PID file
    write_pid(os.getpid())
    
    # Register cleanup
    atexit.register(remove_pid)
    
    return os.getpid()


def start_bot():
    """Start the bot as a daemon."""
    # Check if already running
    existing_pid = read_pid()
    if existing_pid and is_running(existing_pid):
        print(f"Bot is already running (PID: {existing_pid})")
        sys.exit(1)
    
    # Clean up stale PID file
    remove_pid()
    
    # Daemonize
    pid = daemonize()
    print(f"Bot started as daemon (PID: {pid})")
    
    # Now import and run the orchestrator
    # This must happen after daemonization to keep imports clean
    try:
        from orchestrator import main as bot_main
        
        # Set up signal handlers for graceful shutdown
        def signal_handler(signum, frame):
            signame = signal.Signals(signum).name
            print(f"Daemon received {signame}, shutting down...")
            sys.exit(0)
        
        signal.signal(signal.SIGTERM, signal_handler)
        signal.signal(signal.SIGINT, signal_handler)
        
        # Run the bot
        exit_code = asyncio.run(bot_main())
        sys.exit(exit_code)
        
    except Exception as e:
        print(f"Bot crashed: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)


def stop_bot():
    """Stop the running bot."""
    pid = read_pid()
    
    if not pid:
        print("No PID file found - bot may not be running")
        # Try to find and kill by process name as fallback
        import subprocess
        result = subprocess.run(
            ["pkill", "-f", "orchestrator.py"],
            capture_output=True
        )
        if result.returncode == 0:
            print("Stopped bot processes")
        return
    
    if not is_running(pid):
        print(f"Bot not running (stale PID: {pid})")
        remove_pid()
        return
    
    # Send SIGTERM for graceful shutdown
    print(f"Stopping bot (PID: {pid})...")
    os.kill(pid, signal.SIGTERM)
    
    # Wait for process to terminate
    import time
    for _ in range(30):  # Wait up to 30 seconds
        time.sleep(1)
        if not is_running(pid):
            print("Bot stopped successfully")
            remove_pid()
            return
    
    # Force kill if still running
    print("Bot did not stop gracefully, forcing...")
    os.kill(pid, signal.SIGKILL)
    time.sleep(1)
    remove_pid()
    print("Bot force-stopped")


def restart_bot():
    """Restart the bot."""
    stop_bot()
    # Give it a moment to fully release resources
    import time
    time.sleep(2)
    start_bot()


def status_bot():
    """Check bot status."""
    pid = read_pid()
    
    if pid and is_running(pid):
        print(f"Bot is running (PID: {pid})")
        
        # Show recent log
        log_file = BOT_DIR / "logs" / "orchestrator.out"
        if log_file.exists():
            import subprocess
            result = subprocess.run(
                ["tail", "-20", str(log_file)],
                capture_output=True,
                text=True
            )
            print("\nRecent log entries:")
            print(result.stdout)
    else:
        print("Bot is not running")
        if pid:
            print(f"(stale PID file: {pid})")
            remove_pid()


def run_foreground():
    """Run bot in foreground (for testing/debugging)."""
    import asyncio
    from orchestrator import main as bot_main
    
    exit_code = asyncio.run(bot_main())
    sys.exit(exit_code)


def main():
    parser = argparse.ArgumentParser(
        description="Alpaca Trading Bot Daemon Manager"
    )
    parser.add_argument(
        "command",
        choices=["start", "stop", "restart", "status", "foreground"],
        help="Command to execute"
    )
    
    args = parser.parse_args()
    
    if args.command == "start":
        start_bot()
    elif args.command == "stop":
        stop_bot()
    elif args.command == "restart":
        restart_bot()
    elif args.command == "status":
        status_bot()
    elif args.command == "foreground":
        run_foreground()


if __name__ == "__main__":
    main()
