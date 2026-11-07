namespace AutoApprover.Worker.Services;

/// <summary>
/// Einfache Retry-Politik mit linearem Backoff für transienten ADO-API-Fehler.
/// Keine externe Abhängigkeit (Polly etc.), um die Abhängigkeitszahl gering zu halten.
/// </summary>
internal static class RetryPolicy
{
    private const int s_defaultMaxAttempts = 3;
    private static readonly TimeSpan s_defaultDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Führt <paramref name="action"/> bis zu <paramref name="maxAttempts"/> Mal aus.
    /// Bei <see cref="HttpRequestException"/> oder <see cref="TaskCanceledException"/> wird mit
    /// linearem Backoff wiederholt. Andere Ausnahmen werden sofort weitergeworfen.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct,
        int maxAttempts = s_defaultMaxAttempts,
        TimeSpan? delay = null)
    {
        var retryDelay = delay ?? s_defaultDelay;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await action(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                lastException = ex;
                await Task.Delay(retryDelay, ct).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * 1.5);
            }
            catch (TaskCanceledException ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                lastException = ex;
                await Task.Delay(retryDelay, ct).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * 1.5);
            }
            catch (OperationCanceledException ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                lastException = ex;
                await Task.Delay(retryDelay, ct).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * 1.5);
            }
        }

        throw lastException!;
    }

    /// <summary>
    /// Führt <paramref name="action"/> bis zu <paramref name="maxAttempts"/> Mal aus.
    /// Siehe <see cref="ExecuteAsync{T}"/> für das Retry-Verhalten.
    /// </summary>
    public static async Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        CancellationToken ct,
        int maxAttempts = s_defaultMaxAttempts,
        TimeSpan? delay = null)
    {
        await ExecuteAsync(async ct => { await action(ct); return 0; }, ct, maxAttempts, delay).ConfigureAwait(false);
    }
}
