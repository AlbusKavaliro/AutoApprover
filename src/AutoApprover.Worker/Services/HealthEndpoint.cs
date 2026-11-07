using System.Net;
using AutoApprover.Worker.Services;
using Npgsql;

namespace AutoApprover.Worker.Services;

/// <summary>
/// Minimaler HTTP-Listener für Kubernetes Liveness-/Readiness-Probes.
/// Lauscht auf einem eigenen Port (Default 8080) und bedient /healthz sowie /readyz.
/// </summary>
internal sealed class HealthEndpoint(
    IAuditRepository auditRepository,
    IConfiguration configuration,
    ILogger<HealthEndpoint> logger) : IHostedService
{
    private static readonly string s_healthPath = "/healthz";
    private static readonly string s_readyPath = "/readyz";

    private readonly int _port = configuration.GetValue<int>("Health:Port", 8080);
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenerTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener.Prefixes.Add($"http://+:{_port}/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            logger.LogWarning(ex, "Health-Endpoint auf Port {Port} konnte nicht gestartet werden", _port);
            return Task.CompletedTask;
        }

        _listenerTask = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    var path = context.Request.Url?.AbsolutePath ?? string.Empty;

                    if (path.Equals(s_healthPath, StringComparison.OrdinalIgnoreCase))
                    {
                        await RespondAsync(context, HttpStatusCode.OK, "healthy\n");
                    }
                    else if (path.Equals(s_readyPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // Readiness prüft kurz, ob die DB erreichbar ist
                        try
                        {
                            await using var conn = new NpgsqlConnection(
                                configuration.GetConnectionString("AutoApproveDb")
                                ?? Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
                                ?? string.Empty);
                            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
                            await RespondAsync(context, HttpStatusCode.OK, "ready\n");
                        }
                        catch
                        {
                            await RespondAsync(context, HttpStatusCode.ServiceUnavailable, "not ready\n");
                        }
                    }
                    else
                    {
                        await RespondAsync(context, HttpStatusCode.NotFound, "not found\n");
                    }
                }
                catch (HttpListenerException) when (_cts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Fehler im Health-Endpoint");
                }
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        return _listenerTask ?? Task.CompletedTask;
    }

    private static Task RespondAsync(HttpListenerContext context, HttpStatusCode statusCode, string body)
    {
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "text/plain";
        var buffer = System.Text.Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        context.Response.OutputStream.Close();
        return Task.CompletedTask;
    }
}
