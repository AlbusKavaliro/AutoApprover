# Coding-Konventionen – Auto-Approver

## Allgemein

- Ziel-Framework: .NET 11 (RC3), C# 15 (`LangVersion: preview` in `Directory.Build.props`)
- Zentrale Paketverwaltung über `Directory.Packages.props` (Central Package Management). Paketversionen **niemals** direkt in csproj-Dateien angeben.
- Alle Klassen im `Worker`-Projekt sind standardmäßig `internal sealed`, es sei denn, sie müssen öffentlich sein (Modelle, Interfaces).
- Statische Felder verwenden das `s_`-Präfix (gemäß `.editorconfig`).
- Datei-scoped Namespaces (`namespace AutoApprover.Worker;`).
- Imports: System-Namespaces zuerst, dann eigene Namespaces (gemäß `.editorconfig` `dotnet_sort_system_directives_first = true`).

## Logging

- Verwenden Sie `LoggerMessage.Define` statt String-Interpolation im Hot Path (Performance, strukturiertes Logging).
- Event-IDs sind sequenziell (1001, 1002, ...). Neue Event-IDs müssen nicht mit bestehenden kollidieren.
- Logging-Level: `Information` für Entscheidungen, `Debug` für Routine-Prüfungen, `Warning` für erwartete Abweichungen, `Error` für unerwartete Ausnahmen.

## Entscheidungsklassen

- `DecisionEngine.Evaluate` gibt ein `DecisionResult` zurück. Niemals direkt `ApproveCheckAsync` aufrufen ohne vorherige Entscheidung durch `DecisionEngine`.
- `DecisionResult.ChangeType` darf nur Werte aus `{ "dockerfile_digest", "patch", "minor", "none" }` annehmen.
- `DecisionResult.ShouldApprove` darf nur `true` sein, wenn alle Regeln erfüllt sind (Renovate-Ersteller, Datei-Allow-Liste, kein Major, Dockerfile-Digest-Prüfung).

## Dockerfile-Diffs

- `PullRequestInfo.DockerfileDiffs` enthält den Diff-Inhalt (ALT/NEU) für geänderte Dockerfiles.
- Die Digest-Prüfung in `DecisionEngine.IsDockerfileDiffOnlyDigestOrMinorPatch` vergleicht FROM-Zeilen anhand von Regex. Änderungen hier erfordern neue Testfälle in `DecisionEngineSpec.cs`.

## Tests

- Unit-Tests mit xunit. Testklassen enden auf `Spec` (z. B. `DecisionEngineSpec`).
- Test-Methoden beschreiben das erwartete Verhalten auf Deutsch (z. B. `Renovate_PR_mit_nur_Dockerfile_DigestWechsel_wird_genehmigt`).
- Fixtures (`BeispielCheck`, `BeispielPR`) werden als statische Methoden innerhalb der Testklasse definiert.
- `TestLogger` und `ConsoleLogger<T>` stellen einen minimalen `ILogger<T>` für Tests ohne DI bereit.

## Sicherheit

- Keine Secrets im Quellcode hartcodiert (PAT, DB-Connection-String, Teams-Webhook kommen aus Environment-Variablen).
- `traceparent`-Header wird an den ADO Server on-prem gesendet (als sicher eingestuft).
- PAT muss minimalberechtigt sein (nur Environment-Checks lesen/approven, Git lesen).
- SQL-Abfragen verwenden parameterisierte Commands (keine String-Konkatenation).

## Commit-Nachrichten

- Präfixe: `feat:`, `fix:`, `refactor:`, `docs:`, `chore:`, `build:`
- Auf Deutsch
- Kurze, präzise Beschreibung der Änderung

## Offene Konventionen (Nacharbeit)

- Keine Regeln für Graceful Shutdown
- Keine Regeln für Health-Endpoints
- Keine Regeln für Resource Limits in k8s (keine Manifeste im Repo)
