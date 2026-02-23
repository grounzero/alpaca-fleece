# Multi-stage build for AlpacaFleece
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview-alpine AS build
WORKDIR /src

# Copy project files
COPY ["csharp/src/AlpacaFleece.Core/AlpacaFleece.Core.csproj", "src/AlpacaFleece.Core/"]
COPY ["csharp/src/AlpacaFleece.Infrastructure/AlpacaFleece.Infrastructure.csproj", "src/AlpacaFleece.Infrastructure/"]
COPY ["csharp/src/AlpacaFleece.Trading/AlpacaFleece.Trading.csproj", "src/AlpacaFleece.Trading/"]
COPY ["csharp/src/AlpacaFleece.Worker/AlpacaFleece.Worker.csproj", "src/AlpacaFleece.Worker/"]

# Copy source files
COPY ["csharp/src/", "src/"]

# Restore and build
RUN dotnet restore "src/AlpacaFleece.Worker/AlpacaFleece.Worker.csproj"
RUN dotnet build "src/AlpacaFleece.Worker/AlpacaFleece.Worker.csproj" \
    -c Release -o /app/build --no-restore

# Publish
RUN dotnet publish "src/AlpacaFleece.Worker/AlpacaFleece.Worker.csproj" \
    -c Release -o /app/publish

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview-alpine
WORKDIR /app

# Install SQLite, timezone data, and process utilities for healthcheck
RUN apk add --no-cache sqlite-libs tzdata procps

# Copy published application
COPY --from=build /app/publish .

# Create data directory for database, logs, metrics
RUN mkdir -p /app/data /app/logs

# Set environment variables
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_EnableDiagnostics=0

# Volume for persistent data
VOLUME ["/app/data"]

# Health check - verify worker process is running
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
    CMD pgrep -f "AlpacaFleece.Worker" > /dev/null || exit 1

# Entry point
ENTRYPOINT ["dotnet", "AlpacaFleece.Worker.dll"]
