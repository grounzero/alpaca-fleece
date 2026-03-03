#!/bin/bash
# Simple daemon starter using setsid
# setsid creates a new session, detaching from the controlling terminal

# Get the directory where this script is located (portable, no hardcoded paths)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BOT_DIR="$SCRIPT_DIR"
PIDFILE="$BOT_DIR/data/alpaca_bot.pid"

# Ensure directories exist
mkdir -p "$BOT_DIR/data" "$BOT_DIR/logs"

start() {
    if [ -f "$PIDFILE" ]; then
        PID=$(cat "$PIDFILE")
        if kill -0 "$PID" 2>/dev/null; then
            echo "Bot is already running (PID: $PID)"
            exit 1
        else
            echo "Removing stale PID file"
            rm -f "$PIDFILE"
        fi
    fi
    
    echo "Starting Alpaca Trading Bot..."
    cd "$BOT_DIR"
    
    # Start with setsid - creates new session
    # We capture the PID of the python process itself
    (
        cd "$BOT_DIR"
        exec setsid .venv/bin/python -u orchestrator.py >> logs/orchestrator.out 2>&1
    ) &
    
    # Give it a moment to start
    sleep 2
    
    # Find the actual Python process (the orchestrator)
    PID=$(pgrep -f "orchestrator.py" | head -1)
    
    if [ -n "$PID" ]; then
        echo "$PID" > "$PIDFILE"
        echo "Bot started (PID: $PID)"
        echo ""
        echo "Recent log:"
        tail -10 "$BOT_DIR/logs/orchestrator.out"
    else
        echo "Failed to start bot - check logs/orchestrator.out"
        exit 1
    fi
}

stop() {
    if [ ! -f "$PIDFILE" ]; then
        echo "No PID file found - attempting to kill by process name"
        pkill -f "orchestrator.py"
        exit 0
    fi
    
    PID=$(cat "$PIDFILE")
    
    if ! kill -0 "$PID" 2>/dev/null; then
        echo "Bot not running (stale PID: $PID)"
        rm -f "$PIDFILE"
        return
    fi
    
    echo "Stopping bot (PID: $PID)..."
    kill -TERM "$PID"
    
    # Wait for graceful shutdown
    for i in {1..30}; do
        sleep 1
        if ! kill -0 "$PID" 2>/dev/null; then
            echo "Bot stopped successfully"
            rm -f "$PIDFILE"
            return
        fi
    done
    
    echo "Force stopping..."
    kill -KILL "$PID"
    sleep 1
    rm -f "$PIDFILE"
    echo "Bot stopped"
}

restart() {
    stop
    sleep 2
    start
}

status() {
    if [ -f "$PIDFILE" ]; then
        PID=$(cat "$PIDFILE")
        if kill -0 "$PID" 2>/dev/null; then
            echo "Bot is running (PID: $PID)"
            echo ""
            echo "Recent log entries:"
            tail -20 "$BOT_DIR/logs/orchestrator.out"
        else
            echo "Bot is not running (stale PID: $PID)"
        fi
    else
        # Check if running anyway
        PID=$(pgrep -f "orchestrator.py" | head -1)
        if [ -n "$PID" ]; then
            echo "Bot is running (PID: $PID) - no PID file"
            echo "$PID" > "$PIDFILE"
        else
            echo "Bot is not running"
        fi
    fi
}

case "${1:-start}" in
    start)
        start
        ;;
    stop)
        stop
        ;;
    restart)
        restart
        ;;
    status)
        status
        ;;
    *)
        echo "Usage: $0 {start|stop|restart|status}"
        exit 1
        ;;
esac
