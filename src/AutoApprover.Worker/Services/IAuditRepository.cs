using AutoApprover.Worker.Models;

namespace AutoApprover.Worker.Services;

public interface IAuditRepository
{
    Task EnsureSchemaAsync();
    Task WriteAsync(DecisionResult decision, CancellationToken ct = default);
}
