# Process Management Solutions for Alpaca Trading Bot

## Problem
The bot dies when the parent agent session terminates, even with `nohup`. This happens because:
1. `nohup` only ignores SIGHUP for the process itself
2. When using shell backgrounding (`&`), the process remains in the parent's process group
3. On session termination, the shell sends SIGHUP to the entire process group
4. The Python asyncio event loop receives this signal and terminates

## Solutions Provided

### Option 1: Shell Script with `setsid` (Recommended - Simplest)
**File:** `bot.sh`

Uses `setsid` to create a new session, completely detaching from the controlling terminal:

```bash
./bot.sh start   # Start bot
./bot.sh stop    # Stop bot
./bot.sh status  # Check status
```

Or use Make:
```bash
make start
make status
make stop
```

**Why this works:**
- `setsid` creates a new session and process group
- The process is no longer part of the original shell's process group
- SIGHUP sent to the original session doesn't reach the bot

### Option 2: Python Double-Fork Daemon
**File:** `daemon.py`

Uses the Unix double-fork technique for proper daemonization:

```bash
python daemon.py start
python daemon.py stop
python daemon.py status
```

Or use Make:
```bash
make daemon-start
make daemon-status
make daemon-stop
```

**Features:**
- PID file management
- Signal handling
- Proper file descriptor redirection
- Clean start/stop/status interface

### Option 3: Systemd User Service (Most Robust)
**File:** `alpaca-bot.service`

Full systemd integration with automatic restart, logging, and dependency management:

```bash
# Install (one time)
make systemd-install

# Control
make systemd-start
make systemd-stop
make systemd-status

# Auto-start on login
make systemd-enable
```

**Benefits:**
- Automatic restart on crashes
- Structured logging via journald
- Dependency management (network, etc.)
- Clean integration with Linux systems

## Quick Start

```bash
# Recommended: Use the setsid-based shell script
cd /path/to/alpaca-fleece  # Adjust to your installation path
./bot.sh start

# Verify it's running
./bot.sh status

# It will now survive session disconnects!
```

## Testing the Fix

1. Start the bot: `./bot.sh start`
2. Note the PID: `cat data/alpaca_bot.pid`
3. Verify running: `ps aux | grep orchestrator`
4. Exit the session entirely (close terminal/agent)
5. Reconnect and check: `./bot.sh status`

The bot should still be running!

## Technical Details

### Original Failing Command
```bash
nohup python -u orchestrator.py > logs/orchestrator.out 2>&1 &
# Dies on session termination ❌
```

### Working Command
```bash
setsid nohup python -u orchestrator.py >> logs/orchestrator.out 2>&1 &
# Survives session termination ✅
```

### Why `setsid` is Key

In Unix process management:
- Every process belongs to a process group
- Every process group belongs to a session
- A terminal controls a session
- On logout, SIGHUP is sent to ALL processes in the session

`setsid` creates a NEW session with a NEW process group, so the bot is no longer associated with the terminal's session. Even when the terminal closes, the bot's session continues.

## Process Monitoring

The bot is now a true background daemon. To interact with it:

```bash
# Check logs
tail -f logs/orchestrator.out

# Check status
./bot.sh status

# Stop gracefully
./bot.sh stop
```

## Troubleshooting

### Bot won't start
- Check logs: `cat logs/orchestrator.out`
- Check for existing process: `ps aux | grep orchestrator`
- Clean up stale PID: `rm data/alpaca_bot.pid`

### Bot stops anyway
- Check if it's receiving SIGTERM from somewhere
- Verify environment variables are set: `cat .env`
- Check disk space: `df -h`

### Can't stop the bot
- Force kill: `pkill -9 -f orchestrator.py`
- Remove stale PID file: `rm data/alpaca_bot.pid`
