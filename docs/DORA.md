# DORA – Digital Operational Resilience Act

## Einordnung

Der Auto-Approver unterstützt den CD-Prozess (Continuous Deployment) für Anwendungen, die über Azure DevOps Server verteilt werden. Da er direkt in den Produktions-Deploy-Pfad eingreift (Überschreiben von Approvals), ist er aus DORA-Sicht als **kritisch für die Betriebse resilience** einzustufen.

## Relevante DORA-Anforderungen

| Anforderung | Beschreibung | Umsetzungsstand |
|-------------|--------------|-----------------|
| ICT-Risikomanagement | Identifikation und Bewältigung von ICT-Risiken | ⚠️ Teilweise (Risikoanalyse dokumentiert) |
| ICT-Vorfallsmanagement | Melden und Behandeln von ICT-Vorfällen | ❌ Offen (kein Vorfallsmanagement-Prozess definiert) |
| ICT-Drittparteien-Risikomanagement | Überwachung von externen Dienstleistern | ⚠️ Teilweise (ADO Server, PostgreSQL) |
| Resilienztests | Regelmäßige Tests der Betriebse resilience | ❌ Offen |
| Geschäftsfortführung | Sicherstellung der Geschäftskontinuität | ⚠️ Teilweise (Not-Aus über Feature-Flag) |

## Maßnahmen zur DORA-Konformität

### ICT-Risikomanagement

- Risiko: Unerwünschte Produktion freigeben → Minderung durch enge Allow-Liste, Renovate-Bot-Prüfung, Datei-Allow-Liste.
- Risiko: PAT-Kompromittierung → Minderung durch k8s Secret, Minimal Privilege, regelmäßige Rotation.
- Risiko: Falsche Entscheidung durch fehlerhafte Logik → Minderung durch Unit-Tests, Audit-Log, Teams-Benachrichtigung.

### ICT-Vorfallsmanagement (offen)

Es ist ein Prozess zu definieren für:
- Erkennung von fehlerhaften Auto-Approves (z. B. durch fehlgeschlagenes Deployment nach Approval).
- Meldung an die zuständigen Stellen.
- Rollback des Deployments.
- Deaktivierung des Auto-Approvers (Feature-Flag).

### Resilienztests (offen)

Folgende Tests sollten vor Produktivnahme durchgeführt werden:
- Chaos-Test: Was passiert, wenn ADO Server nicht erreichbar ist?
- Chaos-Test: Was passiert, wenn PostgreSQL nicht erreichbar ist?
- Failover-Test: Worker-Pod neu starten, keine Datenverluste im Audit-Log.
- Not-Aus-Test: Feature-Flag auf false setzen, keine neuen Approvals mehr.

## Metriken und Überwachung

Empfohlene DORA-relevante Metriken:
- **Lead Time for Changes:** Zeit von Commit bis Produktion (wird durch Auto-Approver verkürzt).
- **Deployment Frequency:** Häufigkeit der Produktions-Deployments.
- **Change Failure Rate:** Anteil der Deployments, die zu Vorfällen führen (besonders wichtig bei Auto-Approve).
- **Mean Time to Recovery:** Zeit bis zur Wiederherstellung nach einem Vorfall.

Zusätzliche, direkt vom Auto-Approver emittierte Metriken (Meter `AutoApprover.Worker`):

| Metrik | Typ | Labels | DORA-Bezug |
|--------|-----|--------|------------|
| `auto_approve_decisions_total` | Counter | `decision`, `change_type`, `environment_name` | Ermöglicht die Berechnung der automatischen Freigabe-Anteile und unterstützt die Change-Failure-Rate-Analyse bei Auto-Deployments |

Zusätzliche Traces (ActivitySource `AutoApprover.Worker`):

| Aktivität | Beschreibung |
|-----------|--------------|
| `auto.approve.check` | Verfolgt jeden automatischen Approve von der ADO-Abfrage über die Entscheidung bis zum REST-Aufruf, inkl. Tags für Environment, Pipeline, Änderungstyp und PR-ID |

## Offen / Nachzuholen

| Punkt | Status | Empfehlung |
|-------|--------|------------|
| ICT-Vorfallsmanagement-Prozess | ❌ Offen | Prozess definieren und dokumentieren |
| Resilienztests | ❌ Offen | Testplan erstellen und durchführen |
| Risikoregister | ⚠️ Teilweise | Regelmäßig aktualisieren |
| Drittparteien-Überwachung (ADO Server) | ⚠️ Teilweise | SLA und Verfügbarkeit überwachen |
| DORA-Gap-Analyse | ❌ Offen | Interne Audit vor Produktivnahme |
