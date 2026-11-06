using AutoApprover.Worker.Models;
using AutoApprover.Worker.Services;

namespace AutoApprover.Worker;

public sealed class Worker(
    IAdoApiClient adoApiClient,
    IDecisionEngine decisionEngine,
    IAuditRepository auditRepository,
    ITeamsNotifier teamsNotifier,
    IConfiguration configuration,
    ILogger<Worker> logger) : BackgroundService
{
    private readonly TimeSpan _pollingInterval = TimeSpan.FromSeconds(
        configuration.GetValue<int>("AdopSettings:PollingIntervalSeconds", 60));

    private readonly bool _runOnce = configuration.GetValue<bool>("AdopSettings:RunOnce",
        Environment.GetEnvironmentVariable("AUTOAPPROVE_ONCE") is { } envVar &&
        bool.TryParse(envVar, out var parsed) && parsed);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runOnce)
        {
            logger.LogInformation("Auto-Approver Worker im One-Off-Modus (einzelner Durchlauf)");
            await ProcessPendingChecksAsync(stoppingToken);
            logger.LogInformation("Auto-Approver Worker One-Off abgeschlossen.");
            return;
        }

        logger.LogInformation("Auto-Approver Worker gestartet. Polling-Intervall: {Interval}s", _pollingInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingChecksAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unerwarteter Fehler im Polling-Zyklus");
            }

            await Task.Delay(_pollingInterval, stoppingToken);
        }

        logger.LogInformation("Auto-Approver Worker beendet.");
    }

    private async Task ProcessPendingChecksAsync(CancellationToken ct)
    {
        logger.LogDebug("Prüfe auf ausstehende Environment-Checks ...");

        var pendingChecks = await adoApiClient.GetPendingChecksAsync(ct);
        logger.LogInformation("{Count} ausstehende Check-Evaluations gefunden", pendingChecks.Count);

        foreach (var check in pendingChecks)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                await ProcessCheckAsync(check, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fehler bei der Verarbeitung von CheckEvaluation {CheckEvaluationId} in Environment {Environment}", check.Id, check.EnvironmentName);
            }
        }
    }

    private async Task ProcessCheckAsync(PendingCheck check, CancellationToken ct)
    {
        logger.LogInformation("Verarbeite CheckEvaluation {Id} (Environment: {Environment}, Pipeline: {Pipeline})",
            check.Id, check.EnvironmentName, check.PipelineName);

        // 1. PR / Commit auflösen, um den Ersteller und die geänderten Dateien zu prüfen
        var prInfo = await adoApiClient.GetPullRequestForCommitAsync(
            check.ProjectName,
            check.RepositoryId,
            check.SourceCommitId,
            ct);

        // 2. Entscheidung treffen
        var decision = decisionEngine.Evaluate(check, prInfo);

        // 3. Ausführen oder überspringen
        if (decision.ShouldApprove)
        {
            await adoApiClient.ApproveCheckAsync(check.Id, ct);
            logger.LogInformation("CheckEvaluation {Id} automatisch genehmigt. Grund: {Reason}", check.Id, decision.Reason);
        }
        else
        {
            logger.LogInformation("CheckEvaluation {Id} übersprungen. Grund: {Reason}", check.Id, decision.Reason);
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
        }, ct);

        // 5. Teams-Benachrichtigung
        await teamsNotifier.NotifyAsync(decision);
    }
}
