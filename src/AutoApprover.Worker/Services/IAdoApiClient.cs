using AutoApprover.Worker.Models;

namespace AutoApprover.Worker.Services;

internal interface IAdoApiClient
{
    /// <summary>
    /// Liefert alle ausstehende (pending) Check-Evaluations für Environments.
    /// </summary>
    Task<IReadOnlyList<PendingCheck>> GetPendingChecksAsync(CancellationToken ct = default);

    /// <summary>
    /// Löst den zugehörigen Pull-Request für einen Commit auf, inkl. geänderter Dateien.
    /// </summary>
    Task<PullRequestInfo?> GetPullRequestForCommitAsync(
        string projectName,
        string repositoryId,
        string commitId,
        CancellationToken ct = default);

    /// <summary>
    /// Genehmigt eine Check-Evaluation per PATCH (Bypass-Approvals).
    /// </summary>
    Task ApproveCheckAsync(int checkEvaluationId, CancellationToken ct = default);

    /// <summary>
    /// Liefert den Diff-Inhalt aller geänderten Dockerfiles eines PR.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetDockerfileDiffsAsync(
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default);
}
