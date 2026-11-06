using System.Text.Json;
using AutoApprover.Worker.Models;

namespace AutoApprover.Worker.Services;

public sealed class TeamsNotifier(string webhookUrl) : ITeamsNotifier
{
    private static readonly HttpClient HttpClient = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task NotifyAsync(DecisionResult decision)
    {
        if (string.IsNullOrEmpty(webhookUrl))
            return;

        var color = decision.ShouldApprove ? "00FF00" : "FF0000";
        var title = decision.ShouldApprove ? "✅ Auto-Approver: Release genehmigt" : "⏭️ Auto-Approver: Release übersprungen";

        var card = new
        {
            type = "MessageCard",
            summary = title,
            themeColor = color,
            title = title,
            sections = new[]
            {
                new
                {
                    facts = new[]
                    {
                        new { name = "Entscheidung", value = (object?)decision.Decision ?? "–" },
                        new { name = "Grund", value = (object?)decision.Reason ?? "–" },
                        new { name = "Änderungstyp", value = (object?)decision.ChangeType ?? "–" },
                        new { name = "PR-ID", value = (object?)(decision.PullRequestId == 0 ? "–" : decision.PullRequestId.ToString()) ?? "–" },
                        new { name = "PR-Titel", value = (object?)(decision.PullRequestTitle ?? "–") ?? "–" },
                        new { name = "PR-Ersteller", value = (object?)(decision.PullRequestCreator ?? "–") ?? "–" },
                        new { name = "Umgebung", value = (object?)(decision.EnvironmentName ?? "–") ?? "–" },
                        new { name = "Projekt", value = (object?)(decision.ProjectName ?? "–") ?? "–" },
                        new { name = "Zeit", value = (object?)DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC") ?? "–" }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(card, JsonOptions);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        try
        {
            var response = await HttpClient.PostAsync(webhookUrl, content);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            // Teams-Benachrichtigung darf den Worker nicht abstürzen lassen
            System.Console.WriteLine($"Fehler beim Senden der Teams-Nachricht: {ex.Message}");
        }
    }
}
