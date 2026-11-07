# Auto-Approver – AI Agent Kontext

## Projektzweck
Externer .NET Worker Service, der ausstehende Environment-Checks (Approvals) in Azure DevOps Server 2022.2 YAML-Pipelines automatisch genehmigt – nur wenn strenge Regeln erfüllt sind (Renovate-PR, nur Dockerfile-Digest oder Minor/Patch-Updates, kein Major). Läuft als Container in Kubernetes, schreibt Audit-Log in PostgreSQL, benachrichtigt Teams.

## Einstiegspunkte (wichtige Dateien)

| Datei | Zweck |
|-------|-------|
| `src/AutoApprover.Worker/Program.cs` | Host, DI, Konfiguration, OpenTelemetry, DB-Schema-Init, --once Flag |
| `src/AutoApprover.Worker/Worker.cs` | `ApprovalWorker` – BackgroundService, Polling-Loop, Kernfluss, Activity/Meter, Audit-Log-Event |
| `src/AutoApprover.Worker/Services/AdoApiClient.cs` | ADO REST-API: Checks, PR-Auflösung, Diffs, Approve-PATCH, traceparent-Header |
| `src/AutoApprover.Worker/Services/DecisionEngine.cs` | Entscheidungslogik: Renovate-Check, Datei-Allow-Liste, Dockerfile-Digest, Semver |
| `src/AutoApprover.Worker/Services/AuditRepository.cs` | PostgreSQL: Schema, append-only Audit-Log (Trigger), Feature-Flags, DB-Aktivität |
| `src/AutoApprover.Worker/Services/TeamsNotifier.cs` | Teams Incoming Webhook |
| `src/AutoApprover.Worker/Models/AdoModels.cs` | `PendingCheck`, `PullRequestInfo` |
| `src/AutoApprover.Worker/Models/DecisionResult.cs` | Ergebnis der Entscheidungslogik |
| `src/AutoApprover.Worker.Tests/UnitTests/DecisionEngineSpec.cs` | Spec-Tests für die Entscheidungslogik (8 Tests) |
| `Dockerfile` | Multi-Stage, hash-gepinnt, non-root User |
| `.github/workflows/release.yml` | Build, SBOM, SLSA Provenance, Trivy Image-Scan, Cosign-Signing, Push zu GHCR |
| `.github/workflows/trivy-code-scan.yml` | Trivy Filesystem-Scan, SARIF-Upload an Code Scanning |
| `Directory.Packages.props` | Zentrale Paketversionen (Central Package Management) |
| `Directory.Build.props` | Gemeinsame Build-Einstellungen (Analysen, TargetFramework) |
| `global.json` | .NET SDK 11.0.100-rc.3 |

## Build & Test

```bash
# Build
dotnet build src/AutoApprover.Worker/AutoApprover.Worker.csproj

# Test
dotnet test src/AutoApprover.Worker.Tests/AutoApprover.Worker.Tests.csproj

# Lauf
dotnet run --project src/AutoApprover.Worker -- --once
```

## Konfigurations-Schlüssel

| Schlüssel | Quelle | Beschreibung |
|---------|--------|--------------|
| `AdopSettings:OrganizationUrl` | appsettings.json / ADO_URL | ADO Server Base-URL |
| `AdopSettings:Pat` | Umgebungsvariable `ADO_PAT` | Personal Access Token (minimalberechtigt) |
| `AdopSettings:PollingIntervalSeconds` | appsettings.json | Polling-Intervall (Default: 60s) |
| `AdopSettings:RenovatePrCreator` | appsettings.json | Renovate-Bot-Identität (Default: "renovate") |
| `AdopSettings:RunOnce` | `--once` Flag oder `AUTOAPPROVE_ONCE=true` | Einmaliger Durchlauf (CronJob-Modus) |
| `Teams:WebhookUrl` | Umgebungsvariable `TEAMS_WEBHOOK_URL` | Teams Incoming Webhook |
| `ConnectionStrings:AutoApproveDb` | Umgebungsvariable `DB_CONNECTION_STRING` | PostgreSQL-Verbindung |
| `Otlp:Endpoint` | appsettings.json | OTLP Endpoint (Default: http://localhost:4317) |

## Entscheidungsregeln (DecisionEngine)

1. PR muss vorhanden sein (Commit → PR-Auflösung)
2. PR-Ersteller muss `RenovatePrCreator` sein (nicht nur Titel!)
3. Nur erlaubte Dateien: `Dockerfile`, `*.csproj`, Versionsdateien. Keine `.cs`-Logikdateien.
4. Kein Major-Upgrade (Titel/Dateinamen enthalten "major" oder "breaking")
5. Bei Dockerfile: Nur SHA256-Digest-Wechsel oder Minor/Patch-Tag-Wechsel (gleicher Repo, gleiche Major)
6. Feature-Flags `auto_approve_global` und `auto_approve_production` müssen aktiv sein

## Wichtige Hinweise für Änderungen

- **Dockerfile-Digest-Verifizierung:** `DecisionEngine.AnalyzeChangedFiles` prüft den Diff-Inhalt aus `PullRequestInfo.DockerfileDiffs`. Änderungen an der Logik erfordern neue Testfälle in `DecisionEngineSpec.cs`.
- **Append-only Audit-Log:** `AuditRepository` legt PostgreSQL-Trigger an, die UPDATE/DELETE auf `auto_approve_audit_log` blockieren. Nicht entfernen ohne Ersatz.
- **traceparent:** `AdoApiClient.ApproveCheckAsync` sendet `Activity.Current.Id` als `traceparent`-Header an den ADO Server (on-prem, als sicher eingestuft).
- **Keine k8s-Manifeste:** Das Repo enthält bewusst keine Kubernetes-Manifeste oder Compose-Dateien (von Nutzer ausgeschlossen).
- **OpenTelemetry:** Traces (ActivitySource `AutoApprover.Worker`, `AutoApprover.Worker.PostgreSQL`), Metriken (Counter `auto_approve_decisions_total`), Logs über Console-Exporter (stdout für K8s Log-Sidecar).

## Offene Punkte (Nacharbeit)

- Kein Health-Readiness/Liveness-Endpoint für Kubernetes
- Keine Retry-Logik für ADO-API-Ausfälle
- Keine Konfigurationsvalidierung beim Start
- Kein Incident Runbook
- Keine Audit-Log-Retention-Policy
