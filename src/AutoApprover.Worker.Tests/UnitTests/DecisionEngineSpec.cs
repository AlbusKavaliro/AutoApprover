using System;
using System.Collections.Generic;
using AutoApprover.Worker.Models;
using AutoApprover.Worker.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AutoApprover.Worker.Tests.UnitTests;

/// <summary>
/// Spec-Definition für die Auto-Approve-Entscheidungslogik.
///
/// Diese Tests beschreiben das erwartete Verhalten der DecisionEngine:
/// - Nur Renovate-PRs dürfen automatisch genehmigt werden.
/// - Nur Dockerfile- oder Versionsdateien sind erlaubt.
/// - Keine Major-Upgrades.
/// - SHA256-Digest-Wechsel im Dockerfile sind erlaubt (nur Digest, nicht Tag).
/// </summary>
public class DecisionEngineSpec
{
    private static IConfiguration CreateConfiguration(string renovateCreator = "renovate")
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("AdopSettings:RenovatePrCreator", renovateCreator)
            })
            .Build();
    }

    private static PendingCheck BeispielCheck(
        string environmentName,
        string projectName,
        string pipelineName,
        int runId,
        string repositoryId,
        string sourceCommitId,
        string? sourceBranch = "refs/heads/main",
        string? definitionName = "main-ci")
    {
        return new PendingCheck(
            Id: 1,
            EnvironmentName: environmentName,
            ProjectName: projectName,
            PipelineName: pipelineName,
            RunId: runId,
            RepositoryId: repositoryId,
            SourceCommitId: sourceCommitId,
            SourceBranch: sourceBranch,
            DefinitionName: definitionName);
    }

    private static PullRequestInfo BeispielPR(
        int id,
        string title,
        string creator,
        IReadOnlyList<string> changedFiles,
        IReadOnlyDictionary<string, string>? dockerfileDiffs = null,
        string? sourceBranch = "refs/heads/feature",
        string? targetBranch = "refs/heads/main")
    {
        return new PullRequestInfo(
            Id: id,
            Title: title,
            CreatorDisplayName: creator,
            CreatorUniqueName: $"{creator}[bot]",
            SourceBranch: sourceBranch,
            TargetBranch: targetBranch,
            ChangedFilePaths: changedFiles,
            DockerfileDiffs: dockerfileDiffs ?? new Dictionary<string, string>());
    }

    [Fact]
    public void KeinPR_gefunden_wird_nicht_automatisch_genehmigt()
    {
        // Arrange
        var engine = new DecisionEngine(CreateConfiguration(), TestLogger.Create<DecisionEngine>());
        var check = BeispielCheck("anwendung-a-prod", "AppA", "main-ci", 42, "repo-1", "abc123");

        // Act
        var result = engine.Evaluate(check, prInfo: null);

        // Assert
        Assert.False(result.ShouldApprove);
        Assert.Contains("Kein zugehöriger Pull-Request", result.Reason);
        Assert.Equal("none", result.ChangeType);
        Assert.Equal(0, result.PullRequestId);
    }

    [Fact]
    public void PR_ersteller_ist_nicht_renovate_wird_nicht_genehmigt()
    {
        // Arrange
        var engine = new DecisionEngine(CreateConfiguration("renovate"), TestLogger.Create<DecisionEngine>());
        var check = BeispielCheck("anwendung-a-prod", "AppA", "main-ci", 42, "repo-1", "abc123");

        var pr = BeispielPR(
            id: 7,
            title: "Update something",
            creator: "Max Mustermann",
            changedFiles: ["Dockerfile"]);

        // Act
        var result = engine.Evaluate(check, pr);

        // Assert
        Assert.False(result.ShouldApprove);
        Assert.Contains("ist nicht der konfigurierte Renovate-Bot", result.Reason);
        Assert.Equal("none", result.ChangeType);
        Assert.Equal(7, result.PullRequestId);
    }

    [Fact]
    public void Renovate_PR_mit_nur_Dockerfile_DigestWechsel_wird_genehmigt()
    {
        // Arrange
        var engine = new DecisionEngine(CreateConfiguration(), TestLogger.Create<DecisionEngine>());
        var check = BeispielCheck("anwendung-a-prod", "AppA", "main-ci", 42, "repo-1", "abc123");

        var pr = BeispielPR(
            id: 7,
            title: "Update docker base image",
            creator: "renovate",
            changedFiles: ["Dockerfile"],
            dockerfileDiffs: new Dictionary<string, string>
            {
                ["Dockerfile"] = """
=== ALT ===
FROM mcr.microsoft.com/dotnet/aspnet:8.0.10@sha256:a1b2c3d4e5f6...
=== NEU ===
FROM mcr.microsoft.com/dotnet/aspnet:8.0.10@sha256:d4e5f6a7b8c9...
"""
            });

        // Act
        var result = engine.Evaluate(check, pr);

        // Assert
        Assert.True(result.ShouldApprove);
        Assert.Equal("dockerfile_digest", result.ChangeType);
        Assert.Contains("renovate", result.Reason);
        Assert.Equal(7, result.PullRequestId);
    }

    [Fact]
    public void Dockerfile_mit_Tag_Wechsel_Major_wird_uebersprungen()
    {
        // Arrange
        var engine = new DecisionEngine(CreateConfiguration(), TestLogger.Create<DecisionEngine>());
        var check = BeispielCheck("anwendung-a-prod", "AppA", "main-ci", 47, "repo-1", "xyz999");

        var pr = BeispielPR(
            id: 12,
            title: "Update docker base image",
            creator: "renovate",
            changedFiles: ["Dockerfile"],
            dockerfileDiffs: new Dictionary<string, string>
            {
                ["Dockerfile"] = """
=== ALT ===
FROM mcr.microsoft.com/dotnet/aspnet:8.0.10@sha256:a1b2c3d4e5f6...
=== NEU ===
FROM mcr.microsoft.com/dotnet/aspnet:9.0.0@sha256:d4e5f6a7b8c9...
"""
            });

        // Act
        var result = engine.Evaluate(check, pr);

        // Assert
        Assert.False(result.ShouldApprove);
        Assert.Contains("nicht erlaubte Änderung", result.Reason);
    }

    [Fact]
    public void Renovate_PR_mit_nur_Csproj_wird_als_patch_genehmigt()
    {
        // Arrange
        var engine = new DecisionEngine(CreateConfiguration(), TestLogger.Create<DecisionEngine>());
        var check = BeispielCheck("anwendung-a-test", "AppA", "main-ci", 43, "repo-1", "def456");

        var pr = BeispielPR(
            id: 8,
            title: "Bump NuGet package",
            creator: "renovate",
            changedFiles: ["AppA.csproj"]);

        // Act
        var result = engine.Evaluate(check, pr);

        // Assert
        Assert.True(result.ShouldApprove);
        Assert.Equal("patch", result.ChangeType);
    }

    [Fact]
    public void Renovate_PR_mit_CS_Datei_wird_wegen_unerlaubter_Datei_uebersprungen()
    {
        // Arrange
        var engine = new DecisionEngine(CreateConfiguration(), TestLogger.Create<DecisionEngine>());
        var check = BeispielCheck("anwendung-a-prod", "AppA", "main-ci", 44, "repo-1", "ghi789");

        var pr = BeispielPR(
            id: 9,
            title: "Fix bug",
            creator: "renovate",
            changedFiles: ["Services/MyService.cs"]);

        // Act
        var result = engine.Evaluate(check, pr);

        // Assert
        Assert.False(result.ShouldApprove);
        Assert.Contains("Nicht erlaubte Dateien", result.Reason);
        Assert.Equal("none", result.ChangeType);
    }

    [Fact]
    public void Major_Upgrade_im_Titel_wird_uebersprungen()
    {
        // Arrange
        var engine = new DecisionEngine(CreateConfiguration(), TestLogger.Create<DecisionEngine>());
        var check = BeispielCheck("anwendung-a-prod", "AppA", "main-ci", 45, "repo-1", "jkl012");

        var pr = BeispielPR(
            id: 10,
            title: "Major update to .NET 9",
            creator: "renovate",
            changedFiles: ["AppA.csproj"]);

        // Act
        var result = engine.Evaluate(check, pr);

        // Assert
        Assert.False(result.ShouldApprove);
        Assert.Contains("Major-Upgrade", result.Reason);
        Assert.Equal("none", result.ChangeType);
    }

    [Fact]
    public void Gemischte_Dateien_Dockerfile_und_CS_werden_uebersprungen()
    {
        // Arrange
        var engine = new DecisionEngine(CreateConfiguration(), TestLogger.Create<DecisionEngine>());
        var check = BeispielCheck("anwendung-a-prod", "AppA", "main-ci", 46, "repo-1", "mno345");

        var pr = BeispielPR(
            id: 11,
            title: "Update docker and fix code",
            creator: "renovate",
            changedFiles: ["Dockerfile", "Controllers/HomeController.cs"]);

        // Act
        var result = engine.Evaluate(check, pr);

        // Assert
        Assert.False(result.ShouldApprove);
        Assert.Contains("Nicht erlaubte Dateien", result.Reason);
    }
}

// Minimaler Test-Logger, damit DecisionEngine ohne DI instanziiert werden kann
public static class TestLogger
{
    public static Microsoft.Extensions.Logging.ILogger<T> Create<T>()
    {
        return new ConsoleLogger<T>();
    }
}

public class ConsoleLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // Keine Ausgabe in Unit-Tests
    }
}
