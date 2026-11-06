namespace AutoApprover.Worker.Models;

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
