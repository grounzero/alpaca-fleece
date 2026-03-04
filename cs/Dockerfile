FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Copy project files first for layer-cached restore
COPY ["src/AlpacaFleece.Core/AlpacaFleece.Core.csproj", "src/AlpacaFleece.Core/"]
COPY ["src/AlpacaFleece.Infrastructure/AlpacaFleece.Infrastructure.csproj", "src/AlpacaFleece.Infrastructure/"]
COPY ["src/AlpacaFleece.Trading/AlpacaFleece.Trading.csproj", "src/AlpacaFleece.Trading/"]
COPY ["src/AlpacaFleece.Worker/AlpacaFleece.Worker.csproj", "src/AlpacaFleece.Worker/"]

RUN dotnet restore "src/AlpacaFleece.Worker/AlpacaFleece.Worker.csproj"

COPY src/ src/

RUN dotnet publish "src/AlpacaFleece.Worker/AlpacaFleece.Worker.csproj" \
    -c Release -o /app/publish

# ── Runtime ──────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app

RUN apk add --no-cache sqlite-libs tzdata procps

COPY --from=build /app/publish .

RUN mkdir -p /app/data /app/logs /app/config

ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_EnableDiagnostics=0

VOLUME ["/app/data"]

HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
    CMD pgrep -f "AlpacaFleece.Worker" > /dev/null || exit 1

ENTRYPOINT ["dotnet", "AlpacaFleece.Worker.dll"]
