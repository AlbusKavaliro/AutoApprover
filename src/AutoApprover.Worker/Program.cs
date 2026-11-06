using AutoApprover.Worker;
using AutoApprover.Worker.Services;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

// Konfiguration
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                   .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
                   .AddEnvironmentVariables();

// Worker-Dienst
builder.Services.AddHostedService<Worker>();

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

// Decision Engine
builder.Services.AddSingleton<IDecisionEngine, DecisionEngine>();

// Teams Benachrichtigung
builder.Services.AddSingleton<ITeamsNotifier, TeamsNotifier>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var webhookUrl = config["Teams:WebhookUrl"] ?? Environment.GetEnvironmentVariable("TEAMS_WEBHOOK_URL") ?? string.Empty;
    return new TeamsNotifier(webhookUrl);
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

// OpenTelemetry: Logs über Standard-Console-Provider
builder.Logging.AddConsole();

var app = builder.Build();

// Datenbank initialisieren (Tabellen anlegen, falls nicht vorhanden)
using (var scope = app.Services.CreateScope())
{
    var auditRepo = scope.ServiceProvider.GetRequiredService<IAuditRepository>();
    await auditRepo.EnsureSchemaAsync();
}

await app.RunAsync();
