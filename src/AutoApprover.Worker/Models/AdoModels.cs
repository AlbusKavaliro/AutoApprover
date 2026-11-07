using System.Diagnostics.CodeAnalysis;

namespace AutoApprover.Worker.Models;

[SuppressMessage("Design", "CA1515", Justification = "Required to remain public because the tests project constructs it directly.")]
public sealed record PendingCheck(
    int Id,
    string EnvironmentName,
    string ProjectName,
    string PipelineName,
    int RunId,
    string RepositoryId,
    string SourceCommitId,
    string? SourceBranch,
    string? DefinitionName);

[SuppressMessage("Design", "CA1515", Justification = "Required to remain public because the tests project constructs it directly.")]
public sealed record PullRequestInfo(
    int Id,
    string Title,
    string CreatorDisplayName,
    string CreatorUniqueName,
    string? SourceBranch,
    string? TargetBranch,
    IReadOnlyList<string> ChangedFilePaths,
    IReadOnlyDictionary<string, string> DockerfileDiffs); // Pfad -> Diff-Inhalt (alt/neu)

