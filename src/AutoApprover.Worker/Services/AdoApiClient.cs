using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using AutoApprover.Worker.Models;

namespace AutoApprover.Worker.Services;

[SuppressMessage("Design", "CA1812", Justification = "Wird ueber AddHttpClient per DI instanziiert (Reflexion, statisch nicht nachweisbar).")]
internal sealed class AdoApiClient(HttpClient httpClient, ILogger<AdoApiClient> logger) : IAdoApiClient
{
    private const string ApiVersion = "7.2-preview.1";
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Action<ILogger, HttpStatusCode, Exception?> s_logPullRequestsLoadFailed = LoggerMessage.Define<HttpStatusCode>(
        LogLevel.Warning, 3001, "Konnte Pull-Requests nicht laden (Status {StatusCode})");

    private static readonly Action<ILogger, int, HttpStatusCode, Exception?> s_logCheckApproved = LoggerMessage.Define<int, HttpStatusCode>(
        LogLevel.Information, 3002, "CheckEvaluation {Id} erfolgreich genehmigt (Status {StatusCode})");

    public async Task<IReadOnlyList<PendingCheck>> GetPendingChecksAsync(CancellationToken ct = default)
    {
        // CHECKS-API: Liste aller Check-Evaluations mit Status 'pending'
        // Endpoint variiert je nach ADO Server Version; hier der typische Pfad für Azure DevOps Server 2022.x:
        var url = $"_apis/pipelines/checks/check-evaluations?api-version={ApiVersion}&statusFilter=pending";

        using var response = await httpClient.GetAsync(new Uri(url), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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

        using var response = await httpClient.GetAsync(new Uri(url), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            s_logPullRequestsLoadFailed(logger, response.StatusCode, null);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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

            // Geänderte Dateien + Dockerfile-Diffs in einem Aufruf abfragen (gleicher Endpunkt)
            var (changedFiles, dockerfileDiffs) = await GetPullRequestDiffAsync(repositoryId, prId, ct).ConfigureAwait(false);

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
        var body = JsonSerializer.Serialize(new { status = "approved" }, s_jsonOptions);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using var response = await httpClient.PatchAsync(new Uri(url), content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        s_logCheckApproved(logger, checkEvaluationId, response.StatusCode, null);
    }

    private async Task<(IReadOnlyList<string> ChangedFiles, IReadOnlyDictionary<string, string> DockerfileDiffs)> GetPullRequestDiffAsync(
        string repositoryId,
        int pullRequestId,
        CancellationToken ct)
    {
        var url = $"_apis/git/repositories/{repositoryId}/pullrequests/{pullRequestId}/diffs/commits?api-version={ApiVersion}";

        using var response = await httpClient.GetAsync(new Uri(url), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (Array.Empty<string>(), new Dictionary<string, string>());
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var doc = JsonDocument.Parse(json);

        var files = new List<string>();
        var diffs = new Dictionary<string, string>();
        foreach (var change in doc.RootElement.GetProperty("changes").EnumerateArray())
        {
            if (!change.TryGetProperty("item", out var item) ||
                !item.TryGetProperty("path", out var path))
            {
                continue;
            }

            var filePath = path.GetString() ?? string.Empty;
            files.Add(filePath);

            if (!IsDockerfile(filePath.ToUpperInvariant()))
            {
                continue;
            }

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
            {
                diffs[filePath] = sb.ToString();
            }
        }

        return (files, diffs);
    }

    private static bool IsDockerfile(string upperPath) =>
        upperPath.EndsWith("DOCKERFILE", StringComparison.Ordinal) ||
        upperPath.EndsWith(".DOCKERFILE", StringComparison.Ordinal) ||
        upperPath.Contains("DOCKERFILE.", StringComparison.Ordinal);
}
