FROM python:3.12-slim

# Install uv for fast dependency resolution
RUN pip install uv

WORKDIR /app

# Copy dependency files first for layer caching
COPY pyproject.toml ./
COPY uv.lock ./

# Install dependencies using uv
RUN uv sync --frozen --no-dev

# Copy source code
COPY src/ ./src/
COPY main.py ./
COPY config/ ./config/
COPY data/ ./data/

# Create logs directory
RUN mkdir -p logs

# Set environment variables
ENV PYTHONPATH=/app
ENV PYTHONUNBUFFERED=1

# Run the bot
CMD ["uv", "run", "python", "main.py"]
