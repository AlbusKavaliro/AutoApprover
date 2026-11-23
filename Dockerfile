# syntax=docker/dockerfile:1
# ============================================================
# Auto-Approver Worker – Multi-Stage Dockerfile mit Hash-Pinning
# ============================================================

# --- Build Stage ------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:11.0-azurelinux4.0-aot@sha256:b39579f960155ff87f02dea478204bc1f1e2adca6b8f48d70087ce0d5b2cab80 AS build
WORKDIR /src

# Projektdateien kopieren und restore (Layer-Caching für schnellere Builds)
COPY ["Directory.Build.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["src/AutoApprover.Worker/AutoApprover.Worker.csproj", "src/AutoApprover.Worker/"]
COPY ["src/AutoApprover.Worker/packages.lock.json", "src/AutoApprover.Worker/"]
RUN dotnet restore "src/AutoApprover.Worker/AutoApprover.Worker.csproj"

# Quellcode kopieren und veröffentlichen
COPY . .
WORKDIR "/src/src/AutoApprover.Worker"
RUN dotnet publish "AutoApprover.Worker.csproj" -c Release -o /app/publish /p:UseAppHost=false

# --- Runtime Stage ----------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:11.0-azurelinux3.0-distroless@sha256:b6dbf7ea30a494c53fdfc000e0435db1cf1d6f74245e67b9e28ae097f9ab99d9 AS runtime
WORKDIR /app

# Nicht-root User für Container-Sicherheit (BSI IT-Grundschutz / DORA)
USER nonroot

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AutoApprover.Worker.dll"]
