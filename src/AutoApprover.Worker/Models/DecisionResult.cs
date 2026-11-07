using System.Diagnostics.CodeAnalysis;

namespace AutoApprover.Worker.Models;

[SuppressMessage("Design", "CA1515", Justification = "Returned by the public DecisionEngine.Evaluate API and must stay public (CS0050).")]
public sealed record DecisionResult(
    bool ShouldApprove,
    string Reason,
    string ChangeType, // "dockerfile_digest", "patch", "minor", "none"
    int PullRequestId,
    string? PullRequestTitle = null,
    string? PullRequestCreator = null,
    int? RunId = null,
    string? CommitId = null,
    string? EnvironmentName = null,
    string? ProjectName = null,
    string? PipelineName = null,
    string? Decision = null,
    DateTimeOffset? ApprovedAt = null);
