FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Copy project files for layer-cached restore
COPY ["src/AlpacaFleece.Core/AlpacaFleece.Core.csproj", "src/AlpacaFleece.Core/"]
COPY ["src/AlpacaFleece.Infrastructure/AlpacaFleece.Infrastructure.csproj", "src/AlpacaFleece.Infrastructure/"]
COPY ["src/AlpacaFleece.AdminUI/AlpacaFleece.AdminUI.csproj", "src/AlpacaFleece.AdminUI/"]

RUN dotnet restore "src/AlpacaFleece.AdminUI/AlpacaFleece.AdminUI.csproj"

COPY src/ src/

RUN dotnet publish "src/AlpacaFleece.AdminUI/AlpacaFleece.AdminUI.csproj" \
    -c Release -o /app/publish --no-restore

# ── Runtime ──────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app

RUN apk add --no-cache tzdata

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_EnableDiagnostics=0
ENV TZ=UTC

EXPOSE 8080

ENTRYPOINT ["dotnet", "AlpacaFleece.AdminUI.dll"]
