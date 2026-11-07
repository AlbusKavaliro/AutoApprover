# Benutzerhandbuch – Auto-Approver für Azure DevOps Server 2022.2

## Zweck

Der Auto-Approver ist ein externer .NET-Worker, der ausstehende Environment-Checks (Approvals) in YAML-Pipelines von Azure DevOps Server 2022.2 automatisch genehmigen darf – sofern strenge Regeln erfüllt sind. Er dient dazu, einfache Updates (Patch, Minor, Docker-Base-Image-SHA256-Digest) ohne manuellen Klick in Produktion zu bringen.

## Funktionsweise

Der Worker läuft im Hintergrund (Polling, z. B. alle 60 Sekunden) und führt folgende Schritte aus:

1. **Prüfung auf ausstehende Checks** über die Azure DevOps CHECKS-API (`/_apis/pipelines/checks/check-evaluations?statusFilter=pending`).
2. **Auflösung des zugehörigen Pull Requests** über die Git-API (Commit → PR).
3. **Entscheidung** über die `DecisionEngine`:
   - PR-Ersteller muss der konfigurierte Renovate-Bot sein (Platzhalter: `renovate`).
   - Nur erlaubte Dateien: `Dockerfile`, `*.csproj`, Versionsdateien. Keine `.cs`-Logikänderungen.
   - Bei `Dockerfile`: nur SHA256-Digest-Wechsel oder Minor/Patch-Tag-Wechsel bei `FROM`. Kein Major.
   - Kein Major-Upgrade im PR-Titel oder Dateinamen.
4. **Genehmigung** per `PATCH /_apis/pipelines/checks/check-evaluations/{id}` mit `{"status": "approved"}`.
5. **Audit-Log** in PostgreSQL (Entscheidung, Grund, Zeitstempel, PR, Commit, Environment).
6. **Benachrichtigung** an Microsoft Teams via Incoming Webhook.

## Konfiguration

### appsettings.json

```json
{
  "AdopSettings": {
    "OrganizationUrl": "https://devops.company.com/tfs/DefaultCollection",
    "Pat": "",
    "PollingIntervalSeconds": 60,
    "RenovatePrCreator": "renovate"
  },
  "Teams": {
    "WebhookUrl": ""
  },
  "Otlp": {
    "Endpoint": "http://localhost:4317"
  },
  "ConnectionStrings": {
    "AutoApproveDb": "Host=localhost;Database=autoapprover;Username=autoapprover;Password=autoapprover;"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "AutoApprover.Worker": "Debug"
    }
  }
}
```

Wichtige Umgebungsvariablen (überschreiben appsettings.json):
- `ADO_PAT` – Personal Access Token für Azure DevOps Server
- `DB_CONNECTION_STRING` – PostgreSQL-Verbindungszeichenfolge
- `TEAMS_WEBHOOK_URL` – Teams Incoming Webhook URL

### PostgreSQL-Tabellen

Beim Start legt der Worker automatisch folgende Tabellen an (falls nicht vorhanden):

| Tabelle | Zweck |
|---------|-------|
| `environment_auto_approve_config` | Welche Environments dürfen auto-approvt werden |
| `auto_approve_audit_log` | Jede Entscheidung wird hier protokolliert |
| `feature_flags` | Ein-/Ausschalter (z. B. `auto_approve_global`, `auto_approve_production`) |

### Beispiel: Environment-Konfig einfügen

```sql
INSERT INTO environment_auto_approve_config
    (environment_name, project_name, pipeline_id, is_enabled, allowed_pr_creator, allowed_change_types)
VALUES
    ('anwendung-a-prod', 'AppA', NULL, TRUE, 'renovate', ARRAY['dockerfile_digest','patch','minor']),
    ('anwendung-a-test', 'AppA', NULL, TRUE, 'renovate', ARRAY['dockerfile_digest','patch','minor']);
```

### Feature-Flags

```sql
-- Hauptschalter
INSERT INTO feature_flags (name, is_enabled, description) VALUES
('auto_approve_global', TRUE, 'Hauptschalter für den Auto-Approver'),
('auto_approve_production', TRUE, 'Auto-Approver auch in Produktion erlauben');

-- Not-Aus: Produktion deaktivieren
UPDATE feature_flags SET is_enabled = FALSE WHERE name = 'auto_approve_production';
```

## Entscheidungsregeln (zusammengefasst)

| Bedingung | Erlaubt? |
|-----------|----------|
| PR-Ersteller != `renovate` | ❌ Nein |
| Kein zugehöriger PR gefunden | ❌ Nein |
| Datei außerhalb Allow-Liste (z. B. `.cs`, `.cshtml`) | ❌ Nein |
| Major-Upgrade im Titel oder Dateinamen | ❌ Nein |
| Dockerfile: nur SHA256-Digest-Wechsel | ✅ Ja (`dockerfile_digest`) |
| Nur Versionsdatei (`.csproj`, `nuspec`) geändert | ✅ Ja (`patch`) |
| `auto_approve_global` = false | ❌ Nein (globaler Not-Aus) |
| Environment `*-prod` + `auto_approve_production` = false | ❌ Nein |

## Laufweise

- **Entwicklung (lokal):** `dotnet run` mit `ASPNETCORE_ENVIRONMENT=Development`. Nutzt lokale PostgreSQL und OTel Collector.
- **Produktion (k8s):** Als Container-Deployment im Kubernetes-Namespace. PAT als `k8s Secret`. PostgreSQL im selben Namespace.
- **Intervall:** Konfigurierbar via `AdopSettings:PollingIntervalSeconds` (Standard: 60s).

## OpenTelemetry (Traces, Metriken, Logs)

Der Worker emittiert Observability-Daten über OpenTelemetry:

**Traces (ActivitySource):**

| ActivitySource | Aktivität | Kind | Tags |
|----------------|-----------|------|------|
| `AutoApprover.Worker` | `auto.approve.check` | Client | `environment.name`, `pipeline.name`, `change.type`, `pull_request.id`, `decision` |
| `AutoApprover.Worker.PostgreSQL` | `postgresql.ensure_schema` | Client | `db.system`, `db.name`, `db.operation` |
| `AutoApprover.Worker.PostgreSQL` | `postgresql.write_audit_log` | Client | `db.system`, `db.name`, `db.operation`, `db.table`, `decision`, `change_type` |

Die `parentId` jeder Aktivität wird von der aktuellen `Activity` vererbt, sodass der gesamte Entscheidungsfluss (ADO-Abfrage → Entscheidung → DB-Schreibvorgang) in einem verteilten Trace nachvollziehbar ist.

**Metriken (Meter `AutoApprover.Worker`):**

| Metrik | Typ | Labels | Beschreibung |
|--------|-----|--------|--------------|
| `auto_approve_decisions_total` | Counter | `decision`, `change_type`, `environment_name` | Anzahl automatischer und übersprungener Environment-Approvals |

**traceparent-Header:**

Bei jedem erfolgten Auto-Approve wird der aktuelle `traceparent`-Header (aus `Activity.Current.Id`) zusammen mit dem PATCH-Request an den ADO Server gesendet. Dadurch kann der Approve im verteilten Trace des Workers mit der ADO-Seite korreliert werden.

**Logging:**

Logs werden über den Console-Exporter ausgegeben (OpenTelemetry 1.9.0 unterstützt `AddOtlpExporter` für Logging noch nicht direkt). Ein Upgrade auf eine neuere OpenTelemetry-Version ermöglicht den direkten OTLP-Log-Export.

## Teams-Benachrichtigung

Nach jeder Entscheidung (approve / skip / error) wird eine Nachricht an den konfigurierten Teams Incoming Webhook gesendet. Sie enthält:
- Entscheidung und Grund
- Änderungstyp (`dockerfile_digest`, `patch`, `minor`, `none`)
- PR-ID, PR-Titel, PR-Ersteller
- Environment, Projekt, Zeitstempel
