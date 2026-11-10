# syntax=docker/dockerfile:1
# ============================================================
# Auto-Approver Worker – Multi-Stage Dockerfile mit Hash-Pinning
# ============================================================

# --- Build Stage ------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:11.0-azurelinux4.0-aot@sha256:e15f37b39cd56128542d0c05f10d65be344463b4fb0845ea37b372472b55a656 AS build
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
FROM mcr.microsoft.com/dotnet/aspnet:11.0-azurelinux3.0-distroless@sha256:bd252bde098c75a41df4c749f73bb14858a08e7f2429d85812a93e177c503731 AS runtime
WORKDIR /app

# Nicht-root User für Container-Sicherheit (BSI IT-Grundschutz / DORA)
USER nonroot

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AutoApprover.Worker.dll"]
