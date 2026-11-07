# syntax=docker/dockerfile:1
# ============================================================
# Auto-Approver Worker – Multi-Stage Dockerfile mit Hash-Pinning
# ============================================================

# --- Build Stage ------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:11.0-azurelinux4.0-aot@sha256:e44da9920834417edb571919e67f2d82fc346e646e5d02c628f341cb5b1d6463 AS build
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
FROM mcr.microsoft.com/dotnet/aspnet:11.0-azurelinux4.0-distroless@sha256:e8a4fd1b2102d9d4565971922f549a73e25b4a5752eaaf3b5fc3363ecaf80b4b AS runtime
WORKDIR /app

# Nicht-root User für Container-Sicherheit (BSI IT-Grundschutz / DORA)
USER nonroot

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AutoApprover.Worker.dll"]
