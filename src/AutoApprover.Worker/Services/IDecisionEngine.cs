using AutoApprover.Worker.Models;

namespace AutoApprover.Worker.Services;

internal interface IDecisionEngine
{
    /// <summary>
    /// Bewertet, ob eine Check-Evaluation automatisch genehmigt werden darf.
    /// </summary>
    DecisionResult Evaluate(PendingCheck check, PullRequestInfo? prInfo);
}
