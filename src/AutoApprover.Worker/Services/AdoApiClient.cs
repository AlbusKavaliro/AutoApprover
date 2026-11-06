using System.Text.Json;
using AutoApprover.Worker.Models;

namespace AutoApprover.Worker.Services;

public sealed class AdoApiClient(HttpClient httpClient, ILogger<AdoApiClient> logger) : IAdoApiClient
{
    private const string ApiVersion = "7.2-preview.1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PendingCheck>> GetPendingChecksAsync(CancellationToken ct = default)
    {
        // CHECKS-API: Liste aller Check-Evaluations mit Status 'pending'
        // Endpoint variiert je nach ADO Server Version; hier der typische Pfad für Azure DevOps Server 2022.x:
        var url = $"_apis/pipelines/checks/check-evaluations?api-version={ApiVersion}&statusFilter=pending";

        using var response = await httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);

        var checks = new List<PendingCheck>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            checks.Add(new PendingCheck(
                Id: element.GetProperty("id").GetInt32(),
                EnvironmentName: element.GetProperty("environment").GetProperty("name").GetString() ?? string.Empty,
                ProjectName: element.GetProperty("environment").GetProperty("project").GetProperty("name").GetString() ?? string.Empty,
                PipelineName: element.TryGetProperty("pipeline", out var p) ? p.GetProperty("name").GetString() ?? string.Empty : string.Empty,
                RunId: element.TryGetProperty("pipelineRun", out var r) && r.TryGetProperty("id", out var rid) ? rid.GetInt32() : 0,
                RepositoryId: element.TryGetProperty("resource", out var res) && res.TryGetProperty("repository", out var repo) && repo.TryGetProperty("id", out var rid2) ? rid2.GetString() ?? string.Empty : string.Empty,
                SourceCommitId: element.TryGetProperty("resource", out var res2) && res2.TryGetProperty("repository", out var repo2) && repo2.TryGetProperty("version", out var ver) ? ver.GetString() ?? string.Empty : string.Empty,
                SourceBranch: element.TryGetProperty("resource", out var res3) && res3.TryGetProperty("repository", out var repo3) && repo3.TryGetProperty("branch", out var br) ? br.GetString() : null,
                DefinitionName: element.TryGetProperty("definition", out var def) ? def.GetProperty("name").GetString() : null
            ));
        }

        return checks;
    }

    public async Task<PullRequestInfo?> GetPullRequestForCommitAsync(
        string projectName,
        string repositoryId,
        string commitId,
        CancellationToken ct = default)
    {
        // PRs suchen, die den Commit enthalten (lastMergeTargetCommit / lastMergeSourceCommit)
        var url = $"_apis/git/repositories/{repositoryId}/pullrequests?api-version={ApiVersion}&searchCriteria.status=active,completed&searchCriteria.repositoryId={repositoryId}";

        using var response = await httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Konnte Pull-Requests nicht laden (Status {StatusCode})", response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);

        foreach (var pr in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var prId = pr.GetProperty("pullRequestId").GetInt32();
            var title = pr.GetProperty("title").GetString() ?? string.Empty;
            var creator = pr.GetProperty("createdBy");
            var creatorDisplay = creator.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? string.Empty : string.Empty;
            var creatorUnique = creator.TryGetProperty("uniqueName", out var un) ? un.GetString() ?? string.Empty : string.Empty;
            var sourceBranch = pr.GetProperty("sourceRefName").GetString() ?? string.Empty;
            var targetBranch = pr.GetProperty("targetRefName").GetString() ?? string.Empty;

            // Geänderte Dateien des PR abfragen
            var changedFiles = await GetChangedFilesAsync(repositoryId, prId, ct);

            // Dockerfile-Diffs für Digest-Prüfung abfragen
            var dockerfileDiffs = await GetDockerfileDiffsAsync(repositoryId, prId, ct);

            // Prüfung: Enthält der PR den Ziel-Commit als Merge-Ziel?
            if (pr.TryGetProperty("lastMergeTargetCommit", out var targetCommit) &&
                targetCommit.TryGetProperty("commitId", out var tcid) &&
                tcid.GetString() == commitId)
            {
                return new PullRequestInfo(
                    Id: prId,
                    Title: title,
                    CreatorDisplayName: creatorDisplay,
                    CreatorUniqueName: creatorUnique,
                    SourceBranch: sourceBranch,
                    TargetBranch: targetBranch,
                    ChangedFilePaths: changedFiles,
                    DockerfileDiffs: dockerfileDiffs);
            }
        }

        return null;
    }

    public async Task ApproveCheckAsync(int checkEvaluationId, CancellationToken ct = default)
    {
        var url = $"_apis/pipelines/checks/check-evaluations/{checkEvaluationId}?api-version={ApiVersion}";
        var body = JsonSerializer.Serialize(new { status = "approved" }, JsonOptions);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using var response = await httpClient.PatchAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
        logger.LogInformation("CheckEvaluation {Id} erfolgreich genehmigt (Status {StatusCode})", checkEvaluationId, response.StatusCode);
    }

    private async Task<IReadOnlyList<string>> GetChangedFilesAsync(string repositoryId, int pullRequestId, CancellationToken ct)
    {
        var url = $"_apis/git/repositories/{repositoryId}/pullrequests/{pullRequestId}/diffs/commits?api-version={ApiVersion}";

        using var response = await httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return Array.Empty<string>();

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);

        var files = new List<string>();
        foreach (var change in doc.RootElement.GetProperty("changes").EnumerateArray())
        {
            if (change.TryGetProperty("item", out var item) && item.TryGetProperty("path", out var path))
                files.Add(path.GetString() ?? string.Empty);
        }

        return files;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetDockerfileDiffsAsync(
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        var url = $"_apis/git/repositories/{repositoryId}/pullrequests/{pullRequestId}/diffs/commits?api-version={ApiVersion}";

        using var response = await httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return new Dictionary<string, string>();

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);

        var diffs = new Dictionary<string, string>();
        foreach (var change in doc.RootElement.GetProperty("changes").EnumerateArray())
        {
            if (!change.TryGetProperty("item", out var item) ||
                !item.TryGetProperty("path", out var path))
            {
                continue;
            }

            var filePath = path.GetString() ?? string.Empty;
            if (!IsDockerfile(filePath.ToLowerInvariant()))
                continue;

            // Versuche, alten und neuen Inhalt zu extrahieren (je nach ADO Server Version verfügbar)
            var sb = new System.Text.StringBuilder();

            if (change.TryGetProperty("originalContent", out var original) &&
                original.TryGetProperty("content", out var originalContent))
            {
                sb.AppendLine("=== ALT ===");
                sb.AppendLine(originalContent.GetString() ?? string.Empty);
            }

            if (change.TryGetProperty("newContent", out var neu) &&
                neu.TryGetProperty("content", out var newContent))
            {
                sb.AppendLine("=== NEU ===");
                sb.AppendLine(newContent.GetString() ?? string.Empty);
            }

            if (sb.Length > 0)
                diffs[filePath] = sb.ToString();
        }

        return diffs;
    }

    private static bool IsDockerfile(string lowerPath) =>
        lowerPath.EndsWith("dockerfile") ||
        lowerPath.EndsWith(".dockerfile") ||
        lowerPath.Contains("dockerfile.");
}
