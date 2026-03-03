# Multi-stage build for AlpacaFleece Python Bot
FROM python:3.12-slim AS builder

# Install build dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    gcc \
    && rm -rf /var/lib/apt/lists/*

# Install uv package manager
RUN pip install --no-cache-dir uv

WORKDIR /build

# Copy dependency files
COPY requirements.txt requirements-dev.txt constraints.txt ./
COPY pyproject.toml ./

# Install dependencies into a virtual environment
RUN python -m venv /venv && \
    . /venv/bin/activate && \
    pip install --no-cache-dir -r requirements.txt

# Runtime image
FROM python:3.12-slim

# Install runtime dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    sqlite3 \
    tzdata \
    procps \
    && rm -rf /var/lib/apt/lists/*

# Copy virtual environment from builder
COPY --from=builder /venv /venv

# Set up working directory
WORKDIR /app

# Copy application files
COPY src/ ./src/
COPY orchestrator.py daemon.py main.py bot.sh run_bot.sh ./
COPY config/ ./config/
COPY mypy.ini pyproject.toml ./

# Create data and logs directories
RUN mkdir -p /app/data /app/logs

# Set environment variables
ENV PYTHONUNBUFFERED=1
ENV PYTHONPATH=/app
ENV PATH="/venv/bin:$PATH"

# Volume for persistent data
VOLUME ["/app/data", "/app/logs"]

# Health check - verify orchestrator process is running
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
    CMD pgrep -f "orchestrator.py" > /dev/null || exit 1

# Entry point
ENTRYPOINT ["python", "orchestrator.py"]
