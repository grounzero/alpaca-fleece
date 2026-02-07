# Alpaca Bot Stability Fix - Investigation Report

## Date: 2026-02-06
## Issue: Bot silently exiting after 20-30 minutes of runtime

---

## Root Cause Identified

The **housekeeping.start()** method used `asyncio.gather()` without `return_exceptions=True`, meaning if either internal task (`_equity_snapshots` or `_daily_resets`) encountered an unhandled exception, the entire gather would fail and propagate the exception up. This caused:

1. `housekeeping_task` to complete with an exception
2. Main `asyncio.gather()` in `main.py` to return
3. Bot to exit silently without proper shutdown logs

### Why Previous Fix Didn't Work

The previous "fix" removed the external timeout command, but the real issue was internal - unhandled exceptions in the housekeeping subtasks causing cascade failures.

---

## Fixes Applied

### 1. housekeeping.py

**Changed:**
```python
# Before:
await asyncio.gather(*tasks)

# After:
results = await asyncio.gather(*tasks, return_exceptions=True)

# Check for exceptions and log them
for i, result in enumerate(results):
    task_name = "_equity_snapshots" if i == 0 else "_daily_resets"
    if isinstance(result, Exception):
        logger.error(f"Housekeeping task {task_name} failed: {result}", exc_info=result)
```

**Also added:**
- Entry logging for both `_equity_snapshots()` and `_daily_resets()`
- Proper exception handling with CRITICAL level logging
- `CancelledError` re-raising for proper shutdown handling
- `finally` blocks for exit logging

### 2. main.py

**Changed:**
```python
# Before:
await asyncio.gather(
    event_processor_task,
    housekeeping_task,
    watcher_task,
    stream._polling_task,
    return_exceptions=False  # Would raise on any task failure
)

# After:
results = await asyncio.gather(
    event_processor_task,
    housekeeping_task,
    watcher_task,
    stream._polling_task,
    return_exceptions=True  # Catch individual task failures
)

# Check for any exceptions from tasks
task_names = ["event_processor", "housekeeping", "watcher", "polling"]
for name, result in zip(task_names, results):
    if isinstance(result, Exception) and not isinstance(result, asyncio.CancelledError):
        logger.error(f"Task {name} failed with exception: {result}", exc_info=result)
        raise result
```

**Also added:**
- Entry logging for event processor and shutdown watcher
- Exception checking and logging for each task result
- Catch-all exception handler for the main loop

### 3. stream_polling.py

**Added:**
- Entry logging to polling loop
- `finally` block to log when polling loop exits

---

## Verification

- Bot restarted at 21:24 UTC
- Monitoring for 30+ minutes to confirm stability
- New log messages confirming task startup:
  - "Starting main event loop - monitoring all tasks"
  - "Event processor started"
  - "Shutdown watcher started"

---

## Prevention

The key pattern to avoid:
```python
# DANGEROUS - one failing task kills all
tasks = [task1, task2]
await asyncio.gather(*tasks)  # If task1 fails, task2 is cancelled

# SAFE - isolated failures
tasks = [task1, task2]
results = await asyncio.gather(*tasks, return_exceptions=True)
for result in results:
    if isinstance(result, Exception):
        handle_error(result)
```

Always use `return_exceptions=True` when gathering long-running tasks that should be resilient to individual failures.

---

## Files Modified

1. `/home/t-rox/.openclaw/workspace/alpaca-bot/src/housekeeping.py`
2. `/home/t-rox/.openclaw/workspace/alpaca-bot/main.py`
3. `/home/t-rox/.openclaw/workspace/alpaca-bot/src/stream_polling.py`
