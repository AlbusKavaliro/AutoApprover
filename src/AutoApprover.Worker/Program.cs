using AutoApprover.Worker.Services;
using OpenTelemetry.Exporter.OpenTelemetryProtocol;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

// --once Command-Line-Flag für One-Off-Modus (z. B. als CronJob)
if (args.Contains("--once"))
{
    builder.Configuration["AdopSettings:RunOnce"] = "true";
}

// Konfiguration
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                   .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
                   .AddEnvironmentVariables();

// Worker-Dienst
builder.Services.AddHostedService<AutoApprover.Worker.ApprovalWorker>();

// HTTP-Client für Azure DevOps REST-API
builder.Services.AddHttpClient<IAdoApiClient, AdoApiClient>((sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(config["AdopSettings:OrganizationUrl"]!);
    client.DefaultRequestHeaders.Accept.Add(new("application/json"));
    var pat = config["AdopSettings:Pat"] ?? Environment.GetEnvironmentVariable("ADO_PAT") ?? string.Empty;
    if (!string.IsNullOrEmpty(pat))
    {
        var convert = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($":{pat}"));
        client.DefaultRequestHeaders.Authorization = new("Basic", convert);
    }
});

// PostgreSQL Audit-Repository
builder.Services.AddSingleton<IAuditRepository, AuditRepository>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var connString = config.GetConnectionString("AutoApproveDb")
                     ?? Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
                     ?? throw new InvalidOperationException("Connection string 'AutoApproveDb' oder Umgebungsvariable DB_CONNECTION_STRING nicht gefunden.");
    return new AuditRepository(connString);
});

// Health-Endpoint für Kubernetes Liveness-/Readiness-Probes (Port 8080, /healthz, /readyz)
builder.Services.AddHostedService<HealthEndpoint>();

// Decision Engine
builder.Services.AddSingleton<IDecisionEngine, DecisionEngine>();

// Teams Benachrichtigung
builder.Services.AddSingleton<ITeamsNotifier>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var webhookUrl = config["Teams:WebhookUrl"] ?? Environment.GetEnvironmentVariable("TEAMS_WEBHOOK_URL") ?? string.Empty;
    var logger = sp.GetRequiredService<ILogger<TeamsNotifier>>();
    return new TeamsNotifier(Uri.TryCreate(webhookUrl, UriKind.Absolute, out var webhookUri) ? webhookUri : null, logger);
});

// OpenTelemetry: Traces + Metriken mit OTLP-Exporter
var otlpEndpoint = builder.Configuration["Otlp:Endpoint"] ?? "http://localhost:4317";

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("AutoApprover.Worker")
        .AddHttpClientInstrumentation()
        .AddConsoleExporter()
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)))
    .WithMetrics(metrics => metrics
        .AddMeter("AutoApprover.Worker")
        .AddConsoleExporter()
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)));

// Logging: strukturierte Logs über Console-Exporter. In Kubernetes landet stdout/stderr
// bei einem Log-Sidecar (z. B. FluentBit, Promtail), der die Logs an SIEM/SOC-Systeme
// weiterleitet. Das Audit-Log-Event (s_logAuditDecision) enthält alle Felder für die
// Weiterverarbeitung (environment, project_pipeline, change_type, decision, reason, traceparent).
// Ein direkter OTLP-Log-Exporter ist mit der referenzierten OpenTelemetry-Version (1.9.0)
// noch nicht über AddOtlpExporter auf OpenTelemetryLoggerOptions verfügbar.
builder.Logging.AddConsole();

var app = builder.Build();

// Konfiguration validieren bevor der Worker startet
ValidateConfiguration(app.Services.GetRequiredService<IConfiguration>());

// Datenbank initialisieren (Tabellen anlegen, falls nicht vorhanden)
using (var scope = app.Services.CreateScope())
{
    var auditRepo = scope.ServiceProvider.GetRequiredService<IAuditRepository>();
#pragma warning disable CA2007 // EnsureSchemaAsync intentionally runs on the host's synchronization context (application lifetime)
    await auditRepo.EnsureSchemaAsync(app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
#pragma warning restore CA2007
}

#pragma warning disable CA2007 // app.RunAsync() blocks the main thread on the host's synchronization context; ConfigureAwait is intentionally omitted
await app.RunAsync();
#pragma warning restore CA2007

static void ValidateConfiguration(IConfiguration configuration)
{
    var errors = new List<string>();

    var orgUrl = configuration["AdopSettings:OrganizationUrl"] ?? Environment.GetEnvironmentVariable("ADO_ORGANIZATION_URL");
    if (string.IsNullOrWhiteSpace(orgUrl))
        errors.Add("AdopSettings:OrganizationUrl (oder Umgebungsvariable ADO_ORGANIZATION_URL) ist nicht gesetzt.");

    var pat = configuration["AdopSettings:Pat"] ?? Environment.GetEnvironmentVariable("ADO_PAT");
    if (string.IsNullOrWhiteSpace(pat))
        errors.Add("AdopSettings:Pat (oder Umgebungsvariable ADO_PAT) ist nicht gesetzt.");

    var dbConn = configuration.GetConnectionString("AutoApproveDb") ?? Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");
    if (string.IsNullOrWhiteSpace(dbConn))
        errors.Add("ConnectionStrings:AutoApproveDb (oder Umgebungsvariable DB_CONNECTION_STRING) ist nicht gesetzt.");

    if (errors.Count > 0)
    {
        var message = $"Konfigurationsfehler: Der Worker kann nicht starten. Folgende Einstellungen fehlen:{Environment.NewLine}{string.Join(Environment.NewLine, errors.Select(e => $"  - {e}"))}";
        Console.Error.WriteLine(message);
        throw new InvalidOperationException(message);
    }
}
