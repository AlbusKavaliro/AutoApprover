# BSI IT-Grundschutz – Auto-Approver

## Einordnung

Der Auto-Approver ist eine Anwendung im Bereich **APPLICATION SECURITY** mit Anbindung an **Azure DevOps Server** und **PostgreSQL**. Er hat Zugriff auf Produktions-Environments und darf Approvals überschreiben. Daher ist das Risiko als **hoch** einzustufen.

## Relevante Bausteine (ausgewählt)

| Baustein | Bezeichnung | Relevanz |
|----------|-------------|----------|
| APP.4.1 | Sichere Softwareentwicklung | Entwicklung des Workers |
| APP.4.3 | Webanwendungen | REST-API-Anbindung an ADO Server |
| CON.3 | Identitäts- und Zugriffsmanagement | PAT-Berechtigungen, Service-Konto |
| CON.4 | Authentisierung | PAT als Secret, Basic Auth |
| CON.6 | Berechtigungsmanagement | Minimal Privilege für PAT |
| OPS.1.1.1 | Festlegung des Betriebs | Betriebshandbuch, Not-Aus |
| OPS.1.1.2 | Erstellung und Pflege von Dokumentation | Dieses Handbuch |
| OPS.3.1 | Schwachstellenmanagement | Patchen von .NET Runtime, NuGet-Paketen |
| OPS.4.1 | Datensicherung | PostgreSQL-Backups |

## Umgesetzte Maßnahmen

### Identitäts- und Zugriffsmanagement (CON.3 / CON.4 / CON.6)

- Der PAT wird als k8s Secret gespeichert, nicht im Image.
- Der PAT ist auf die minimal notwendigen Rechte beschränkt (Environment-Checks lesen/approven, Pipelines lesen, Git lesen).
- Keine Hardcoding von Secrets im Quellcode.
- Basic Auth über Authorization-Header (Base64-encodiert).

### Betrieb (OPS.1.1)

- Betriebshandbuch vorhanden (siehe BETRIEBHANDBUCH.md).
- Not-Aus über Feature-Flag in PostgreSQL (`auto_approve_global`).
- Audit-Log aller Entscheidungen in PostgreSQL. **Tamper-Proof**: Die Tabelle `auto_approve_audit_log` ist append-only – UPDATE/DELETE werden auf Datenbankebene durch einen `BEFORE UPDATE`/`BEFORE DELETE`-Trigger blockiert (auch für den Connecting-User).
- Strukturiertes Logging mit OpenTelemetry.
- Tracing: Aktivitäten `auto.approve.check` (ADO-Approve) sowie `postgresql.ensure_schema` / `postgresql.write_audit_log` (DB-Zugriffe) mit `db.*`-Tags.
- Metriken: Counter `auto_approve_decisions_total` mit Labels `decision`, `change_type`, `environment_name`.
- `traceparent`-Header wird bei jedem automatischen Approve an den ADO Server mitgesendet (W3C Trace Context).
- SIEM/SOC-Bereitstellung: Strukturiertes Audit-Log-Event pro Entscheidung über Console-Exporter (stdout → K8s Log-Sidecar → SIEM). Felder: `environment`, `project_pipeline`, `change_type`, `decision`, `reason`, `traceparent`.

### Datensicherung (OPS.4.1)

- PostgreSQL-Datenbank enthält Audit-Log und Konfiguration.
- Regelmäßige Backups (pg_dump) werden empfohlen.

## Offen / Nachzuholen

| Punkt | Status | Empfehlung |
|-------|--------|------------|
| Penetrationstest des Workers | ❌ Offen | Vor Produktivnahme durchführen |
| Regelmäßiges Patchen der NuGet-Pakete | ⚠️ Teilweise | `dotnet list package --outdated` überwachen |
| Klassifizierung der Daten (Audit-Log) | ❌ Offen | Audit-Log als vertraulich einstufen |
| Rollen- und Konzeptdokumentation | ❌ Offen | Rollen für PAT-Verwaltung definieren |
| BSI-Grundschutz-Konformitätsprüfung | ❌ Offen | Interner Audit vor Inbetriebnahme |

## Risiko: Automatische Produktion freigeben

Die automatische Freigabe von Produktions-Deployments ohne menschliche Prüfung ist ein **hohes Risiko**. Minderung:
- Sehr enge Allow-Liste (nur bestimmte Environments, nur bestimmte Pipelines).
- PR-Ersteller muss exakt der Renovate-Bot sein.
- Nur Dockerfile-Digest-Wechsel oder Minor/Patch-Updates.
- Globaler Not-Aus über Feature-Flag.
- Teams-Benachrichtigung nach jeder Entscheidung.
