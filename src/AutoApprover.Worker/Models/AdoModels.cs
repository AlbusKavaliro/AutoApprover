namespace AutoApprover.Worker.Models;

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

public sealed record PullRequestInfo(
    int Id,
    string Title,
    string CreatorDisplayName,
    string CreatorUniqueName,
    string SourceBranch,
    string TargetBranch,
    IReadOnlyList<string> ChangedFilePaths,
    IReadOnlyDictionary<string, string> DockerfileDiffs); // Pfad -> Diff-Inhalt (alt/neu)

public sealed record GitDiffChange(
    string Path,
    string? ChangeType); // Add, Edit, Delete, Rename
