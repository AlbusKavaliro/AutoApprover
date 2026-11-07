using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using AutoApprover.Worker.Models;
using AutoApprover.Worker.Services;

namespace AutoApprover.Worker;

[SuppressMessage("Design", "CA1812", Justification = "Wird ueber AddHostedService per DI instanziiert (Reflexion, statisch nicht nachweisbar).")]
internal sealed class ApprovalWorker(
    IAdoApiClient adoApiClient,
    IDecisionEngine decisionEngine,
    IAuditRepository auditRepository,
    ITeamsNotifier teamsNotifier,
    IConfiguration configuration,
    ILogger<ApprovalWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> s_logOneOffMode = LoggerMessage.Define(
        LogLevel.Information, 1001, "Auto-Approver Worker im One-Off-Modus (einzelner Durchlauf)");

    private static readonly Action<ILogger, Exception?> s_logOneOffCompleted = LoggerMessage.Define(
        LogLevel.Information, 1002, "Auto-Approver Worker One-Off abgeschlossen.");

    private static readonly Action<ILogger, double, Exception?> s_logStarted = LoggerMessage.Define<double>(
        LogLevel.Information, 1003, "Auto-Approver Worker gestartet. Polling-Intervall: {Interval}s");

    private static readonly Action<ILogger, Exception?> s_logPollingError = LoggerMessage.Define(
        LogLevel.Error, 1004, "Unerwarteter Fehler im Polling-Zyklus");

    private static readonly Action<ILogger, Exception?> s_logEnded = LoggerMessage.Define(
        LogLevel.Information, 1005, "Auto-Approver Worker beendet.");

    private static readonly Action<ILogger, Exception?> s_logChecking = LoggerMessage.Define(
        LogLevel.Debug, 1006, "Pruefe auf ausstehende Environment-Checks ...");

    private static readonly Action<ILogger, int, Exception?> s_logPendingCount = LoggerMessage.Define<int>(
        LogLevel.Information, 1007, "{Count} ausstehende Check-Evaluations gefunden");

    private static readonly Action<ILogger, int, string, Exception?> s_logProcessingError = LoggerMessage.Define<int, string>(
        LogLevel.Error, 1008, "Fehler bei der Verarbeitung von CheckEvaluation {CheckEvaluationId} in Environment {Environment}");

    private static readonly Action<ILogger, int, string, string, Exception?> s_logProcessingCheck = LoggerMessage.Define<int, string, string>(
        LogLevel.Information, 1009, "Verarbeite CheckEvaluation {Id} (Environment: {Environment}, Pipeline: {Pipeline})");

    private static readonly Action<ILogger, int, string, Exception?> s_logCheckApproved = LoggerMessage.Define<int, string>(
        LogLevel.Information, 1010, "CheckEvaluation {Id} automatisch genehmigt. Grund: {Reason}");

    private static readonly Action<ILogger, int, string, Exception?> s_logCheckSkipped = LoggerMessage.Define<int, string>(
        LogLevel.Information, 1011, "CheckEvaluation {Id} uebersprungen. Grund: {Reason}");

    private static readonly ActivitySource s_activitySource = new("AutoApprover.Worker");
    private static readonly Meter s_meter = new("AutoApprover.Worker");
    private static readonly Counter<int> s_autoApproveCounter = s_meter.CreateCounter<int>(
        "auto_approve_decisions_total",
        "Anzahl automatischer Environment-Approvals");

    private readonly TimeSpan _pollingInterval = TimeSpan.FromSeconds(
        configuration.GetValue<int>("AdopSettings:PollingIntervalSeconds", 60));

    private readonly bool _runOnce = configuration.GetValue<bool>("AdopSettings:RunOnce", false);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runOnce)
        {
            s_logOneOffMode(logger, null);
            await ProcessPendingChecksAsync(stoppingToken).ConfigureAwait(false);
            s_logOneOffCompleted(logger, null);
            return;
        }

        s_logStarted(logger, _pollingInterval.TotalSeconds, null);

        while (!stoppingToken.IsCancellationRequested)
        {
#pragma warning disable CA1031 // Ein Worker-Polling-Loop faengt bewusst alle Ausnahme ab, um den Loop am Laufen zu halten
            try
            {
                await ProcessPendingChecksAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                s_logPollingError(logger, ex);
            }
#pragma warning restore CA1031

            await Task.Delay(_pollingInterval, stoppingToken).ConfigureAwait(false);
        }

        s_logEnded(logger, null);
    }

    private async Task ProcessPendingChecksAsync(CancellationToken ct)
    {
        s_logChecking(logger, null);

        var pendingChecks = await adoApiClient.GetPendingChecksAsync(ct).ConfigureAwait(false);
        s_logPendingCount(logger, pendingChecks.Count, null);

        foreach (var check in pendingChecks)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await ProcessCheckAsync(check, ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Pro-Check-Fehler faengt bewusst alle Ausnahme ab, damit einzelne Checks den Loop nicht abbrechen
            catch (Exception ex)
            {
                s_logProcessingError(logger, check.Id, check.EnvironmentName, ex);
            }
#pragma warning restore CA1031
        }
    }

    private async Task ProcessCheckAsync(PendingCheck check, CancellationToken ct)
    {
        s_logProcessingCheck(logger, check.Id, check.EnvironmentName, check.PipelineName, null);

        // 1. PR / Commit auflösen, um den Ersteller und die geänderten Dateien zu prüfen
        var prInfo = await adoApiClient.GetPullRequestForCommitAsync(
            check.ProjectName,
            check.RepositoryId,
            check.SourceCommitId,
            ct).ConfigureAwait(false);

        // 2. Entscheidung treffen
        var decision = decisionEngine.Evaluate(check, prInfo);

        // 3. Ausführen oder überspringen
        if (decision.ShouldApprove)
        {
            using var activity = s_activitySource.StartActivity(
                "auto.approve.check",
                ActivityKind.Client,
                parentId: Activity.Current?.Id);

            activity?.SetTag("environment.name", check.EnvironmentName);
            activity?.SetTag("pipeline.name", check.PipelineName);
            activity?.SetTag("change.type", decision.ChangeType);
            activity?.SetTag("pull_request.id", decision.PullRequestId);
            activity?.SetTag("decision", "approved");

            await adoApiClient.ApproveCheckAsync(check.Id, ct).ConfigureAwait(false);

            s_autoApproveCounter.Add(
                1,
                new KeyValuePair<string, object?>("decision", "approved"),
                new KeyValuePair<string, object?>("change_type", decision.ChangeType),
                new KeyValuePair<string, object?>("environment_name", check.EnvironmentName));

            s_logCheckApproved(logger, check.Id, decision.Reason, null);
        }
        else
        {
            s_logCheckSkipped(logger, check.Id, decision.Reason, null);
        }

        // 4. Audit-Log in PostgreSQL
        await auditRepository.WriteAsync(decision with
        {
            RunId = check.RunId,
            CommitId = check.SourceCommitId,
            EnvironmentName = check.EnvironmentName,
            ProjectName = check.ProjectName,
            PipelineName = check.PipelineName,
            Decision = decision.ShouldApprove ? "approved" : "skipped",
            Reason = decision.Reason,
            ApprovedAt = decision.ShouldApprove ? DateTimeOffset.UtcNow : null
        }, ct).ConfigureAwait(false);

        // 5. Teams-Benachrichtigung
        await teamsNotifier.NotifyAsync(decision).ConfigureAwait(false);
    }
}
