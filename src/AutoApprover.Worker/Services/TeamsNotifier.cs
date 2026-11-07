using System.Globalization;
using System.Text.Json;
using AutoApprover.Worker.Models;

namespace AutoApprover.Worker.Services;

internal sealed class TeamsNotifier(
    Uri? webhookUrl,
    ILogger<TeamsNotifier> logger) : ITeamsNotifier
{
    private static readonly HttpClient s_httpClient = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Action<ILogger, string, Exception?> s_logTeamsNotificationSent = LoggerMessage.Define<string>(
        LogLevel.Information, 2001, "Teams-Benachrichtigung gesendet (Entscheidung: {Decision})");

    private static readonly Action<ILogger, string, Exception?> s_logTeamsNotificationError = LoggerMessage.Define<string>(
        LogLevel.Warning, 2002, "Fehler beim Senden der Teams-Nachricht: {Message}");

    public async Task NotifyAsync(DecisionResult decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (webhookUrl is null)
        {
            return;
        }

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
                        new { name = "Entscheidung", value = (object?)(decision.Decision ?? "–") },
                        new { name = "Grund", value = (object?)(decision.Reason ?? "–") },
                        new { name = "Änderungstyp", value = (object?)(decision.ChangeType ?? "–") },
                        new { name = "PR-ID", value = (object?)(decision.PullRequestId == 0 ? "–" : decision.PullRequestId.ToString(CultureInfo.InvariantCulture)) },
                        new { name = "PR-Titel", value = (object?)(decision.PullRequestTitle ?? "–") },
                        new { name = "PR-Ersteller", value = (object?)(decision.PullRequestCreator ?? "–") },
                        new { name = "Umgebung", value = (object?)(decision.EnvironmentName ?? "–") },
                        new { name = "Projekt", value = (object?)(decision.ProjectName ?? "–") },
                        new { name = "Zeit", value = (object?)(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture)) }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(card, s_jsonOptions);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        try
        {
            var response = await s_httpClient.PostAsync(webhookUrl, content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            s_logTeamsNotificationSent(logger, decision.Decision ?? string.Empty, null);
        }
#pragma warning disable CA1031 // Die Teams-Benachrichtigung darf den Worker nicht abstuerzen lassen; alle Ausnahmefaelle werden protokolliert
        catch (Exception ex)
        {
            s_logTeamsNotificationError(logger, ex.Message, ex);
        }
#pragma warning restore CA1031
    }
}
