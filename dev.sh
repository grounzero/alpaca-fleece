#!/bin/bash
# Dev workflow script - build once, run many times with volume mounts

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Load env vars
if [ -f .env ]; then
    export $(grep -v '^#' .env | xargs)
fi

usage() {
    echo "Usage: $0 [command]"
    echo ""
    echo "Commands:"
    echo "  build     - Build base dev image (run once)"
    echo "  run       - Run with volume mounts (fast iteration)"
    echo "  shell     - Start container with shell for debugging"
    echo "  test      - Run tests with volume mounts"
    echo "  logs      - Show container logs"
    echo "  stop      - Stop dev container"
    echo "  clean     - Remove dev container and volumes"
    echo ""
    echo "Examples:"
    echo "  $0 build && $0 run     # First time setup"
    echo "  $0 run                  # After code changes (no rebuild)"
    echo "  $0 test                 # Run tests"
}

build_base() {
    echo -e "${GREEN}Building base dev image...${NC}"
    docker build -t alpaca-fleece-cs:dev-base -f cs/Dockerfile ./cs
    echo -e "${GREEN}Base image built: alpaca-fleece-cs:dev-base${NC}"
}

run_dev() {
    echo -e "${GREEN}Starting dev container with volume mounts...${NC}"
    
    # Stop existing container if running
    docker stop alpaca-fleece-dev 2>/dev/null || true
    docker rm alpaca-fleece-dev 2>/dev/null || true
    
    # Run with volume mounts
    docker run -d \
        --name alpaca-fleece-dev \
        -e ASPNETCORE_ENVIRONMENT=Development \
        -e Broker__ApiKey="${ALPACA_API_KEY:-$Broker__ApiKey}" \
        -e Broker__SecretKey="${ALPACA_SECRET_KEY:-$Broker__SecretKey}" \
        -v "$(pwd)/cs/src:/src/src:ro" \
        -v alpaca-fleece-data:/app/data \
        -v "$(pwd)/cs/logs:/app/logs" \
        alpaca-fleece-cs:dev-base
    
    echo -e "${GREEN}Dev container started!${NC}"
    echo "View logs: $0 logs"
}

run_shell() {
    echo -e "${YELLOW}Starting container with shell access...${NC}"
    docker stop alpaca-fleece-dev 2>/dev/null || true
    docker rm alpaca-fleece-dev 2>/dev/null || true
    
    docker run -it \
        --name alpaca-fleece-dev \
        -e ASPNETCORE_ENVIRONMENT=Development \
        -e Broker__ApiKey="${ALPACA_API_KEY:-$Broker__ApiKey}" \
        -e Broker__SecretKey="${ALPACA_SECRET_KEY:-$Broker__SecretKey}" \
        -v "$(pwd)/cs/src:/src/src:ro" \
        -v alpaca-fleece-data:/app/data \
        -v "$(pwd)/cs/logs:/app/logs" \
        --entrypoint /bin/sh \
        alpaca-fleece-cs:dev-base
}

run_tests() {
    echo -e "${GREEN}Running tests with volume mounts...${NC}"
    docker run --rm \
        -v "$(pwd)/cs/src:/src/src:ro" \
        -v "$(pwd)/cs/tests:/src/tests:ro" \
        -w /src \
        mcr.microsoft.com/dotnet/sdk:10.0-alpine \
        dotnet test tests/AlpacaFleece.Tests/AlpacaFleece.Tests.csproj
}

show_logs() {
    docker logs --tail 50 -f alpaca-fleece-dev 2>&1 | grep -E "DBG|INF|WRN|ERR"
}

stop_dev() {
    echo -e "${YELLOW}Stopping dev container...${NC}"
    docker stop alpaca-fleece-dev 2>/dev/null || true
}

clean_dev() {
    echo -e "${RED}Cleaning up dev environment...${NC}"
    docker stop alpaca-fleece-dev 2>/dev/null || true
    docker rm alpaca-fleece-dev 2>/dev/null || true
    docker volume rm alpaca-fleece-data 2>/dev/null || true
    echo -e "${GREEN}Cleanup complete${NC}"
}

# Main command handler
case "${1:-}" in
    build)
        build_base
        ;;
    run)
        run_dev
        ;;
    shell)
        run_shell
        ;;
    test)
        run_tests
        ;;
    logs)
        show_logs
        ;;
    stop)
        stop_dev
        ;;
    clean)
        clean_dev
        ;;
    *)
        usage
        exit 1
        ;;
esac
