#!/usr/bin/env bash
# Run the AdminUI in development mode.
# Usage: ./run-admin-dev.sh [password]
# Defaults to password "admin" if not supplied.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

# Generate or use a password
PASSWORD="${1:-admin}"
echo "Generating password hash for: $PASSWORD"
HASH=$(dotnet run --project tools/GenPasswordHash -- "$PASSWORD" 2>/dev/null)
echo "Hash: $HASH"
echo ""

# Point config at actual Worker appsettings if no local copy exists
if [ ! -f config/appsettings.json ]; then
    mkdir -p config
    cp src/AlpacaFleece.Worker/appsettings.json config/appsettings.json
    echo "Copied Worker appsettings.json to config/"
fi

# Create empty dirs so the app doesn't crash on missing paths
mkdir -p data logs

echo "Starting AdminUI on http://localhost:5001 ..."
echo "Login password: $PASSWORD"
echo ""

ASPNETCORE_ENVIRONMENT=Development \
ADMIN_PASSWORD_HASH="$HASH" \
Admin__AdminPasswordHash="$HASH" \
Admin__BotSettingsPath="$SCRIPT_DIR/config/appsettings.json" \
Admin__DatabasePath="$SCRIPT_DIR/data/trading.db" \
Admin__LogPath="$SCRIPT_DIR/logs/alpaca-fleece.log" \
Admin__MetricsPath="$SCRIPT_DIR/data/metrics.json" \
Admin__HealthPath="$SCRIPT_DIR/data/health.json" \
ASPNETCORE_URLS="http://localhost:5001" \
dotnet run --project src/AlpacaFleece.AdminUI
