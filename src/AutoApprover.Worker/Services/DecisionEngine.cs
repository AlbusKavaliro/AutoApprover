using AutoApprover.Worker.Models;

namespace AutoApprover.Worker.Services;

public sealed class DecisionEngine(
    IConfiguration configuration,
    ILogger<DecisionEngine> logger) : IDecisionEngine
{
    private readonly string _renovateCreator = configuration["AdopSettings:RenovatePrCreator"] ?? "renovate";
    private readonly ILogger<DecisionEngine> _logger = logger;

    public DecisionResult Evaluate(PendingCheck check, PullRequestInfo? prInfo)
    {
        // Kein PR gefunden -> nicht automatisch genehmigen
        if (prInfo is null)
            return new DecisionResult(false, "Kein zugehöriger Pull-Request gefunden", "none", 0);

        // 1. PR-Ersteller prüfen (nicht nur Titel!)
        if (!string.Equals(prInfo.CreatorDisplayName, _renovateCreator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(prInfo.CreatorUniqueName, _renovateCreator, StringComparison.OrdinalIgnoreCase))
        {
            return new DecisionResult(
                false,
                $"PR-Ersteller '{prInfo.CreatorDisplayName}' ist nicht der konfigurierte Renovate-Bot ('{_renovateCreator}')",
                "none",
                prInfo.Id,
                prInfo.Title,
                prInfo.CreatorDisplayName);
        }

        // 2. Geänderte Dateien prüfen
        var fileAnalysis = AnalyzeChangedFiles(prInfo.ChangedFilePaths);
        if (!fileAnalysis.IsAllowed)
            return new DecisionResult(
                false,
                $"Nicht erlaubte Dateien im PR: {string.Join(", ", fileAnalysis.DisallowedFiles)}",
                "none",
                prInfo.Id,
                prInfo.Title,
                prInfo.CreatorDisplayName);

        // 3. Semver: Kein Major-Upgrade (prüfe Titel und Dateinamen)
        if (IsMajorUpgrade(prInfo.Title, prInfo.ChangedFilePaths))
            return new DecisionResult(
                false,
                "Major-Upgrade erkannt (nicht automatisch genehmigbar)",
                "none",
                prInfo.Id,
                prInfo.Title,
                prInfo.CreatorDisplayName);

        // 4. Entscheidung positiv
        var changeType = fileAnalysis.ChangeType;
        _logger.LogInformation("Auto-Approve zugelassen für PR {PrId} ({PrTitle}) durch {Creator}", prInfo.Id, prInfo.Title, prInfo.CreatorDisplayName);
        return new DecisionResult(
            true,
            $"Auto-Approved: PR-Ersteller={prInfo.CreatorDisplayName}, Änderungstyp={changeType}, Dateien={string.Join(", ", prInfo.ChangedFilePaths)}",
            changeType,
            prInfo.Id,
            prInfo.Title,
            prInfo.CreatorDisplayName);
    }

    private static FileAnalysis AnalyzeChangedFiles(IReadOnlyList<string> changedFiles)
    {
        var disallowed = new List<string>();
        var dockerfileChanges = new List<string>();
        var versionFileChanges = new List<string>();

        foreach (var file in changedFiles)
        {
            var lower = file.ToLowerInvariant();

            if (IsDockerfile(lower))
            {
                dockerfileChanges.Add(file);
            }
            else if (IsVersionFile(lower))
            {
                versionFileChanges.Add(file);
            }
            else
            {
                disallowed.Add(file);
            }
        }

        if (disallowed.Count > 0)
            return new FileAnalysis(false, "none", disallowed);

        // Nur Dockerfile-Änderungen: Prüfung auf SHA256-Digest oder Minor/Patch
        if (dockerfileChanges.Count > 0 && versionFileChanges.Count == 0)
        {
            // Vereinfachte Prüfung: Der Aufrufer (AdoApiClient) liefert nur die Pfade.
            // Die tatsächliche Digest-Prüfung müsste über den Diff-Inhalt erfolgen.
            // Hier markieren wir es als dockerfile_digest; die Detailprüfung erfolgt im AdoApiClient oder hier über den Diff-Inhalt.
            return new FileAnalysis(true, "dockerfile_digest", Array.Empty<string>());
        }

        // Nur Versionsdateien: als patch/minor behandeln
        if (versionFileChanges.Count > 0 && dockerfileChanges.Count == 0)
            return new FileAnalysis(true, "patch", Array.Empty<string>());

        // Gemischt oder leer
        return changedFiles.Count == 0
            ? new FileAnalysis(false, "none", Array.Empty<string>())
            : new FileAnalysis(true, "patch", Array.Empty<string>());
    }

    private static bool IsMajorUpgrade(string title, IReadOnlyList<string> files)
    {
        var lowerTitle = title.ToLowerInvariant();
        if (lowerTitle.Contains("major") || lowerTitle.Contains("breaking"))
            return true;

        foreach (var file in files)
        {
            var lower = file.ToLowerInvariant();
            if (lower.Contains("major") || lower.Contains("breaking"))
                return true;
        }

        return false;
    }

    private static bool IsDockerfile(string lowerPath) =>
        lowerPath.EndsWith("dockerfile") ||
        lowerPath.EndsWith(".dockerfile") ||
        lowerPath.Contains("dockerfile.");

    private static bool IsVersionFile(string lowerPath) =>
        lowerPath.EndsWith("csproj") ||
        lowerPath.EndsWith("nuspec") ||
        lowerPath.EndsWith("version.json") ||
        lowerPath.EndsWith("assemblyinfo.cs");

    private sealed record FileAnalysis(bool IsAllowed, string ChangeType, IReadOnlyList<string> DisallowedFiles);
}
