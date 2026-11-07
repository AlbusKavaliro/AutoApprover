using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using AutoApprover.Worker.Models;

namespace AutoApprover.Worker.Services;

[SuppressMessage("Design", "CA1515", Justification = "Die Tests instanziieren DecisionEngine direkt; der Typ muss public bleiben.")]
public sealed class DecisionEngine(
    IConfiguration configuration,
    ILogger<DecisionEngine> logger) : IDecisionEngine
{
    private readonly string _renovateCreator = configuration["AdopSettings:RenovatePrCreator"] ?? "renovate";
    private readonly ILogger<DecisionEngine> _logger = logger;

    // FROM mcr.microsoft.com/dotnet/aspnet:8.0.10@sha256:a1b2c3...
    private static readonly Regex s_fromLineRegex = new(
        @"^FROM\s+(.+?)(?::([^@\s]+))?(?:@sha256:([0-9a-f]+))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly string[] s_dockerfileDiffMarkers = { "=== ALT ===", "=== NEU ===" };

    private static readonly Action<ILogger, int, string, string, Exception?> s_logAutoApproved = LoggerMessage.Define<int, string, string>(
        LogLevel.Information, 4001, "Auto-Approve zugelassen für PR {PrId} ({PrTitle}) durch {Creator}");

    private static readonly Action<ILogger, string, Exception?> s_logNoDockerfileDiff = LoggerMessage.Define<string>(
        LogLevel.Warning, 4002, "Kein Diff-Inhalt fuer Dockerfile {Dockerfile} verfuegbar. PR wird uebersprungen.");

    public DecisionResult Evaluate(PendingCheck check, PullRequestInfo? prInfo)
    {
        // Kein PR gefunden -> nicht automatisch genehmigen
        if (prInfo is null)
        {
            return new DecisionResult(false, "Kein zugehöriger Pull-Request gefunden", "none", 0);
        }

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

        // 2. Geänderte Dateien prüfen (inkl. Dockerfile-Digest-Verifikation)
        var fileAnalysis = AnalyzeChangedFiles(prInfo);
        if (!fileAnalysis.IsAllowed)
        {
            return new DecisionResult(
                false,
                $"Nicht erlaubte Dateien im PR: {string.Join(", ", fileAnalysis.DisallowedFiles)}",
                "none",
                prInfo.Id,
                prInfo.Title,
                prInfo.CreatorDisplayName);
        }

        // 3. Semver: Kein Major-Upgrade (prüfe Titel und Dateinamen)
        if (IsMajorUpgrade(prInfo.Title, prInfo.ChangedFilePaths))
        {
            return new DecisionResult(
                false,
                "Major-Upgrade erkannt (nicht automatisch genehmigbar)",
                "none",
                prInfo.Id,
                prInfo.Title,
                prInfo.CreatorDisplayName);
        }

        // 4. Entscheidung positiv
        var changeType = fileAnalysis.ChangeType;
        s_logAutoApproved(_logger, prInfo.Id, prInfo.Title, prInfo.CreatorDisplayName, null);
        return new DecisionResult(
            true,
            $"Auto-Approved: PR-Ersteller={prInfo.CreatorDisplayName}, Änderungstyp={changeType}, Dateien={string.Join(", ", prInfo.ChangedFilePaths)}",
            changeType,
            prInfo.Id,
            prInfo.Title,
            prInfo.CreatorDisplayName);
    }

    private FileAnalysis AnalyzeChangedFiles(PullRequestInfo prInfo)
    {
        var disallowed = new List<string>();
        var dockerfileChanges = new List<string>();
        var versionFileChanges = new List<string>();

        foreach (var file in prInfo.ChangedFilePaths)
        {
            var upper = file.ToUpperInvariant();

            if (IsDockerfile(upper))
            {
                dockerfileChanges.Add(file);
            }
            else if (IsVersionFile(upper))
            {
                versionFileChanges.Add(file);
            }
            else
            {
                disallowed.Add(file);
            }
        }

        if (disallowed.Count > 0)
        {
            return new FileAnalysis(false, "none", disallowed);
        }

        // Nur Dockerfile-Änderungen: tatsächlichen Diff-Inhalt prüfen
        if (dockerfileChanges.Count > 0 && versionFileChanges.Count == 0)
        {
            foreach (var dockerfile in dockerfileChanges)
            {
                if (!prInfo.DockerfileDiffs.TryGetValue(dockerfile, out var diffContent) ||
                    string.IsNullOrWhiteSpace(diffContent))
                {
                    // Kein Diff-Inhalt verfügbar -> konservativ: nicht genehmigen
                    s_logNoDockerfileDiff(_logger, dockerfile, null);
                    return new FileAnalysis(
                        false,
                        "none",
                        new[] { $"Dockerfile {dockerfile}: kein Diff-Inhalt verfuegbar" });
                }

                if (!IsDockerfileDiffOnlyDigestOrMinorPatch(diffContent))
                {
                    return new FileAnalysis(
                        false,
                        "none",
                        new[] { $"Dockerfile {dockerfile}: nicht erlaubte Änderung (mehr als Digest oder Minor/Patch)" });
                }
            }

            return new FileAnalysis(true, "dockerfile_digest", Array.Empty<string>());
        }

        // Nur Versionsdateien: als patch/minor behandeln
        if (versionFileChanges.Count > 0 && dockerfileChanges.Count == 0)
        {
            return new FileAnalysis(true, "patch", Array.Empty<string>());
        }

        // Gemischt oder leer
        return prInfo.ChangedFilePaths.Count == 0
            ? new FileAnalysis(false, "none", Array.Empty<string>())
            : new FileAnalysis(true, "patch", Array.Empty<string>());
    }

    /// <summary>
    /// Prüft, ob der Dockerfile-Diff nur SHA256-Digest-Wechsel oder
    /// Minor/Patch-Tag-Wechsel in FROM-Zeilen enthält.
    /// </summary>
    private static bool IsDockerfileDiffOnlyDigestOrMinorPatch(string diffContent)
    {
        // Trenne alt und neu anhand der Marker
        var parts = diffContent.Split(s_dockerfileDiffMarkers, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var oldContent = parts[0];
        var newContent = parts[1];

        var oldFromLines = ExtractFromLines(oldContent);
        var newFromLines = ExtractFromLines(newContent);

        // Anzahl der FROM-Zeilen muss identisch sein
        if (oldFromLines.Count != newFromLines.Count)
        {
            return false;
        }

        for (int i = 0; i < oldFromLines.Count; i++)
        {
            if (!IsFromLineChangeAllowed(oldFromLines[i], newFromLines[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> ExtractFromLines(string content)
    {
        var lines = new List<string>();
        foreach (Match match in s_fromLineRegex.Matches(content))
        {
            lines.Add(match.Value);
        }
        return lines;
    }

    /// <summary>
    /// Prüft, ob eine FROM-Zeilen-Änderung erlaubt ist.
    /// Erlaubt:
    ///   - Nur Digest-Wechsel (gleicher Repo+Tag, anderer @sha256)
    ///   - Minor/Patch Tag-Wechsel (gleiche Major-Version)
    /// Nicht erlaubt:
    ///   - Repo-Wechsel
    ///   - Major Tag-Wechsel
    ///   - Tag hinzugefügt/entfernt
    /// </summary>
    private static bool IsFromLineChangeAllowed(string oldFrom, string newFrom)
    {
        var oldMatch = s_fromLineRegex.Match(oldFrom);
        var newMatch = s_fromLineRegex.Match(newFrom);

        if (!oldMatch.Success || !newMatch.Success)
        {
            return false;
        }

        var oldRepo = oldMatch.Groups[1].Value.Trim();
        var oldTag = oldMatch.Groups[2].Value;
        var oldDigest = oldMatch.Groups[3].Value;

        var newRepo = newMatch.Groups[1].Value.Trim();
        var newTag = newMatch.Groups[2].Value;
        var newDigest = newMatch.Groups[3].Value;

        // Repository darf sich nicht ändern
        if (!string.Equals(oldRepo, newRepo, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Wenn beide keine Tags haben (nur @sha256), nur Digest-Wechsel erlaubt
        if (string.IsNullOrEmpty(oldTag) && string.IsNullOrEmpty(newTag))
        {
            return !string.Equals(oldDigest, newDigest, StringComparison.OrdinalIgnoreCase);
        }

        // Tags müssen beide vorhanden sein oder beide fehlen
        if (string.IsNullOrEmpty(oldTag) != string.IsNullOrEmpty(newTag))
        {
            return false;
        }

        // Beide haben Tags
        if (!string.IsNullOrEmpty(oldTag) && !string.IsNullOrEmpty(newTag))
        {
            var oldMajor = GetMajorVersion(oldTag);
            var newMajor = GetMajorVersion(newTag);

            if (oldMajor != newMajor)
            {
                return false; // Major-Wechsel nicht erlaubt
            }

            // Wenn Digest sich geändert hat und Tags identisch sind -> erlaubt (Digest-Update)
            if (string.Equals(oldTag, newTag, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(oldDigest, newDigest, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Wenn Tags unterschiedlich sind (aber gleiche Major) -> Minor/Patch, erlaubt
            if (!string.Equals(oldTag, newTag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Alles identisch -> keine Änderung
            return false;
        }

        return false;
    }

    private static int GetMajorVersion(string tag)
    {
        // Entferne führendes 'v' falls vorhanden
        var clean = tag.StartsWith('v', StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;
        var match = Regex.Match(clean, @"^(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : int.MaxValue;
    }

    private static bool IsMajorUpgrade(string title, IReadOnlyList<string> files)
    {
        var upperTitle = title.ToUpperInvariant();
        if (upperTitle.Contains("MAJOR", StringComparison.Ordinal) ||
            upperTitle.Contains("BREAKING", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var file in files)
        {
            var upper = file.ToUpperInvariant();
            if (upper.Contains("MAJOR", StringComparison.Ordinal) ||
                upper.Contains("BREAKING", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDockerfile(string upperPath) =>
        upperPath.EndsWith("DOCKERFILE", StringComparison.Ordinal) ||
        upperPath.EndsWith(".DOCKERFILE", StringComparison.Ordinal) ||
        upperPath.Contains("DOCKERFILE.", StringComparison.Ordinal);

    private static bool IsVersionFile(string upperPath) =>
        upperPath.EndsWith("CSPROJ", StringComparison.Ordinal) ||
        upperPath.EndsWith("NUSPEC", StringComparison.Ordinal) ||
        upperPath.EndsWith("VERSION.JSON", StringComparison.Ordinal) ||
        upperPath.EndsWith("ASSEMBLYINFO.CS", StringComparison.Ordinal);

    private sealed record FileAnalysis(bool IsAllowed, string ChangeType, IReadOnlyList<string> DisallowedFiles);
}
