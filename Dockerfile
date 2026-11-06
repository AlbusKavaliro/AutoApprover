# syntax=docker/dockerfile:1
# ============================================================
# Auto-Approver Worker – Multi-Stage Dockerfile mit Hash-Pinning
# ============================================================
#
# WICHTIG: Die @sha256:... Werte unten sind PLACEHOLDER.
# Ersetzen Sie sie vor dem ersten Build durch die echten Digests der
# Microsoft Base-Images. Ermitteln Sie diese z. B. so:
#
#   docker pull mcr.microsoft.com/dotnet/sdk:11.0.100-rc.3.26522.148 --quiet
#   docker images --digests | grep mcr.microsoft.com/dotnet/sdk
#
# Tragen Sie den ermittelten Digest bei @sha256: ein.
# ============================================================

# --- Build Stage ------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.3.26522.148@sha256:REPLACE_WITH_SDK_DIGEST AS build
WORKDIR /src

# Projektdateien kopieren und restore (Layer-Caching für schnellere Builds)
COPY ["src/AutoApprover.Worker/AutoApprover.Worker.csproj", "src/AutoApprover.Worker/"]
RUN dotnet restore "src/AutoApprover.Worker/AutoApprover.Worker.csproj"

# Quellcode kopieren und veröffentlichen
COPY . .
WORKDIR "/src/src/AutoApprover.Worker"
RUN dotnet publish "AutoApprover.Worker.csproj" -c Release -o /app/publish /p:UseAppHost=false

# --- Runtime Stage ----------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:11.0@sha256:REPLACE_WITH_RUNTIME_DIGEST AS runtime
WORKDIR /app

# Nicht-root User für bessere Sicherheit (BSI IT-Grundschutz / DORA)
RUN adduser --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AutoApprover.Worker.dll"]
