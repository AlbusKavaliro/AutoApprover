# Betriebshandbuch – Auto-Approver

## Architektur

```
ADO Server 2022.2 ←→ .NET Worker (k8s) ←→ PostgreSQL (k8s) ←→ Teams Webhook
```

## Komponenten

| Komponente | Beschreibung |
|------------|--------------|
| `.NET Worker` | .NET 11 BackgroundService, pollt CHECKS-API, trifft Entscheidung, schreibt Audit-Log |
| `PostgreSQL` | Speichert Environment-Konfig, Audit-Log, Feature-Flags |
| `PAT Secret` | k8s Secret mit Azure DevOps PAT (nur lesen + checks approven) |
| `OTel` | OpenTelemetry für Traces (ActivitySource), Metriken (Meter) und Logs (Console-Exporter) |

## Deployment (Kubernetes)

Der Worker läuft als `Deployment` mit `replicas: 1`. Wichtige Konfiguration:

```yaml
env:
- name: ADO_PAT
  valueFrom:
    secretKeyRef:
      name: ado-pat
      key: pat
- name: DB_CONNECTION_STRING
  valueFrom:
    secretKeyRef:
      name: autoapprover-db
      key: connection-string
- name: TEAMS_WEBHOOK_URL
  valueFrom:
    secretKeyRef:
      name: teams-webhook
      key: url
```

### CronJob vs. Deployment

Der Worker kann als `Deployment` (langlaufend mit Polling) oder als `CronJob` (alle X Minuten) betrieben werden.

Bei **CronJob** empfiehlt sich `concurrencyPolicy: Forbid`, um Überschneidungen zu vermeiden:

```yaml
spec:
  concurrencyPolicy: Forbid
```

Bei **Deployment** läuft immer genau eine Instanz (replicas: 1), wodurch Überschneidungen ausgeschlossen sind.

## Betrieb

### Starten

```bash
dotnet run --project src/AutoApprover.Worker
```

### Gesundheit prüfen

Der Worker gibt über OpenTelemetry Traces, Metriken und Logs aus:

**Traces (ActivitySource):**

| ActivitySource | Aktivität | Wichtige Tags |
|----------------|-----------|---------------|
| `AutoApprover.Worker` | `auto.approve.check` | `environment.name`, `pipeline.name`, `change.type`, `pull_request.id`, `decision` |
| `AutoApprover.Worker.PostgreSQL` | `postgresql.ensure_schema` | `db.system`, `db.name`, `db.operation` |
| `AutoApprover.Worker.PostgreSQL` | `postgresql.write_audit_log` | `db.system`, `db.name`, `db.operation`, `db.table`, `decision`, `change_type` |

**Metriken (Meter `AutoApprover.Worker`):**

| Metrik | Typ | Labels | Beschreibung |
|--------|-----|--------|--------------|
| `auto_approve_decisions_total` | Counter | `decision`, `change_type`, `environment_name` | Anzahl automatischer und übersprungener Environment-Approvals |

**traceparent:**

Bei jedem erfolgten Auto-Approve wird der aktuelle `traceparent`-Header (aus `Activity.Current.Id`) an den ADO Server mitgesendet, sodass der Approve im verteilten Trace korreliert werden kann.

**Logs:**

Strukturiertes Logging über den Console-Exporter mit Informationen zu jeder Entscheidung (approve / skip / error, Grund, PR-ID, Environment).

Empfohlene Alerts:
- Worker nicht erreichbar / Pod nicht Ready
- Hohe Anzahl übersprungener Checks (mögliche Fehlkonfiguration)
- Fehler bei ADO-API-Aufrufen (PAT abgelaufen, Netzwerk)
- Fehler beim Schreiben in PostgreSQL
- Hohe Rate an `auto_approve_decisions_total` mit `decision=approved` in Produktion (ungewöhnlich viele automatische Freigaben)

### Not-Aus

Bei Problemen kann der Auto-Approver sofort deaktiviert werden:

```sql
UPDATE feature_flags SET is_enabled = FALSE WHERE name = 'auto_approve_global';
```

Der Worker liest das Flag bei jedem Polling-Zyklus neu. Ein Neustart ist nicht erforderlich.

### PAT-Rotation

Der PAT ist als k8s Secret hinterlegt. Bei Rotation:
1. Neues Secret erstellen
2. Deployment auf neues Secret zeigen
3. Rollout auslösen

Der PAT sollte **minimalberechtigt** sein (nur Environment-Checks lesen + approven, Pipelines lesen, Git lesen). Kein globaler Admin.

## Backup & Wiederherstellung

Die PostgreSQL-Datenbank enthält:
- Environment-Konfiguration (wiederherstellbar aus Konfiguration)
- Audit-Log (wichtige Compliance-Daten, sollte regelmäßig gesichert werden)
- Feature-Flags (wiederherstellbar aus Konfiguration)

Regelmäßige pg_dump-Backups werden empfohlen.

## Überwachung (Monitoring)

Empfohlene Alerts:
- Worker nicht erreichbar / Pod nicht Ready
- Hohe Anzahl übersprungener Checks (mögliche Fehlkonfiguration)
- Fehler bei ADO-API-Aufrufen (PAT abgelaufen, Netzwerk)
- Fehler beim Schreiben in PostgreSQL
