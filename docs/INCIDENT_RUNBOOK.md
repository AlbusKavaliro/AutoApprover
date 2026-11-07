# Incident Runbook – Auto-Approver

Dieses Runbook beschreibt die wichtigsten Vorfallszenarien für den Auto-Approver und die entsprechenden Maßnahmen.

---

## 1. PAT abgelaufen oder widerrufen

**Symptom:**
- Worker-Logs zeigen `401 Unauthorized` bei ADO-API-Aufrufen
- Keine neuen Auto-Approves mehr
- Health-Endpoint `/readyz` bleibt grün (DB ist erreichbar), aber keine Entscheidungen werden getroffen

**Maßnahmen:**
1. Neuen PAT in Azure DevOps Server erstellen (Personale Zugangstoken, minimalberechtigt)
2. Als k8s Secret `ado-pat` aktualisieren:
   ```bash
   kubectl create secret generic ado-pat --from-literal=pat=<neuer-PAT> --dry-run=client -o yaml | kubectl apply -f -
   ```
3. Rollout auslösen:
   ```bash
   kubectl rollout restart deployment/auto-approver-worker
   ```
4. Prüfen, ob der Worker wieder Entscheidungen trifft (Logs, Teams-Benachrichtigungen)

**Prävention:** PAT-Rotation alle 90 Tage, Alert bei Ablauf in < 14 Tagen.

---

## 2. ADO Server nicht erreichbar

**Symptom:**
- Worker-Logs zeigen `HttpRequestException` oder Timeout bei `GetPendingChecksAsync`
- Keine neuen Checks werden verarbeitet
- Retry-Logik versucht es 3-mal mit Backoff, dann gibt der Zyklus auf und wartet auf den nächsten Intervall

**Maßnahmen:**
1. ADO Server-Status prüfen (Server-Statusseite, Netzwerkverbindung)
2. Firewall/Proxy-Regeln zwischen Worker-Namespace und ADO Server prüfen
3. Bei längerem Ausfall (> 15 Min): Manuelles Approve der betroffenen Releases in ADO durchführen
4. Nach Wiederherstellung: Worker erholt sich automatisch beim nächsten Polling-Zyklus

**Prävention:** Alert bei wiederholten `HttpRequestException` im Worker-Log.

---

## 3. PostgreSQL nicht erreichbar

**Symptom:**
- Worker-Logs zeigen `NpgsqlException` bei `EnsureSchemaAsync` oder `WriteAsync`
- `/readyz` gibt `503 Service Unavailable` zurück
- Entscheidungen werden getroffen, aber nicht in die Audit-Tabelle geschrieben (Fehler wird geloggt)

**Maßnahmen:**
1. PostgreSQL-Pod-Status prüfen: `kubectl get pods -l app=postgres`
2. Persistent Volume Claim prüfen
3. Verbindungszeichenfolge (`DB_CONNECTION_STRING`) verifizieren
4. Bei Datenbank-Wiederherstellung: Worker erholt sich automatisch

**Prävention:** Alert bei `/readyz` = 503, regelmäßige pg_dump-Backups.

---

## 4. Falscher Auto-Approve in Produktion

**Symptom:**
- Ein Release wurde automatisch approved, das nicht hätte approved werden dürfen
- Deployment in Produktion läuft oder ist bereits erfolgt

**Maßnahmen (sofort):**
1. **Not-Aus:** Feature-Flag `auto_approve_production` auf `FALSE` setzen:
   ```sql
   UPDATE feature_flags SET is_enabled = FALSE WHERE name = 'auto_approve_production';
   ```
   Der Worker liest das Flag bei jedem Polling-Zyklus neu, kein Neustart erforderlich.
2. **Deployment stoppen:** Falls das Deployment noch läuft, im ADO Server manuell stoppen oder rollbacken.
3. **Audit-Log prüfen:** In `auto_approve_audit_log` nach dem entsprechenden Eintrag suchen (Umgebung, Commit, PR, Zeitstempel).
4. **Ursache analysieren:** Warum wurde die Entscheidung getroffen? Welche Regel wurde verletzt?

**Nachbereitung:**
- Entscheidung in Post-Mortem dokumentieren
- Renovate-Bot-Identität prüfen (wurde ein nicht-Renovate-PR als renovate getarnt?)
- Datei-Allow-Liste prüfen (wurde eine nicht erlaubte Datei geändert?)
- Bei Bedarf: Allow-Liste enger fassen

**Prävention:** Enge Allow-Liste, nur bestimmte Environments, nur bestimmte Pipelines, PR-Ersteller muss exakt `renovate` sein.

---

## 5. Worker-Pod stürzt wiederholt ab (CrashLoopBackOff)

**Symptom:**
- `kubectl get pods` zeigt `CrashLoopBackOff`
- Worker-Start schlägt fehl

**Maßnahmen:**
1. Pod-Logs prüfen: `kubectl logs <pod-name> --previous`
2. Häufige Ursachen:
   - Fehlende Konfiguration (Start schlägt fehl wegen `ValidateConfiguration`)
   - DB nicht erreichbar beim Start (`EnsureSchemaAsync` schlägt fehl)
   - ADO Server nicht erreichbar (Ping/Timeout)
3. Konfiguration korrigieren und Rollout auslösen:
   ```bash
   kubectl rollout restart deployment/auto-approver-worker
   ```

---

## 6. Audit-Log wächst unbegrenzt

**Symptom:**
- PostgreSQL-Speicherplatz wird knapp
- `auto_approve_audit_log` Tabelle hat Millionen von Einträgen

**Maßnahmen:**
1. Retention-Policy prüfen (siehe `docs/BETRIEBHANDBUCH.md`, Abschnitt Backup & Wiederherstellung)
2. Alte Einträge archivieren oder löschen (da Tabelle append-only ist, erfordert DELETE Superuser-Rechte oder temporäres Droppen des Triggers):
   ```sql
   -- Trigger temporär deaktivieren
   ALTER TABLE auto_approve_audit_log DISABLE TRIGGER prevent_audit_log_update;
   ALTER TABLE auto_approve_audit_log DISABLE TRIGGER prevent_audit_log_delete;
   
   -- Alte Einträge löschen (älter als 365 Tage)
   DELETE FROM auto_approve_audit_log WHERE created_at < NOW() - INTERVAL '365 days';
   
   -- Trigger wieder aktivieren
   ALTER TABLE auto_approve_audit_log ENABLE TRIGGER prevent_audit_log_update;
   ALTER TABLE auto_approve_audit_log ENABLE TRIGGER prevent_audit_log_delete;
   ```
3. Automatisierten Bereinigungs-Job einrichten (z. B. wöchentlicher CronJob)

**Prävention:** Retention-Policy dokumentieren und regelmäßig prüfen.

---

## 7. Teams-Benachrichtigung kommt nicht an

**Symptom:**
- Worker-Logs zeigen `Fehler beim Senden der Teams-Nachricht`
- Keine Teams-Nachrichten trotz Entscheidungen

**Maßnahmen:**
1. Teams Webhook URL prüfen (`TEAMS_WEBHOOK_URL` Umgebungsvariable)
2. Webhook-Endpunkt testen (manueller POST-Request)
3. Firewall/Proxy zwischen Worker und Teams-Webhook prüfen
4. Worker-Rollout auslösen nach Korrektur der URL

**Prävention:** Webhook-URL in Secret rotieren, wenn kompromittiert.

---

## 8. Not-Aus (global)

**Sofortmaßnahme:**
```sql
UPDATE feature_flags SET is_enabled = FALSE WHERE name = 'auto_approve_global';
```
Der Worker stoppt alle automatischen Approves beim nächsten Polling-Zyklus. Kein Neustart erforderlich.

**Wiederherstellung:**
```sql
UPDATE feature_flags SET is_enabled = TRUE WHERE name = 'auto_approve_global';
```

---

## Kontakt und Eskalation

| Rolle | Kontakt |
|-------|---------|
| Plattform-Team (Kubernetes, ADO) | - |
| Sicherheits-Team (PAT, Zugriff) | - |
| Datenbank-Team (PostgreSQL) | - |
| Anwendungsteam (Auto-Approver) | - |
