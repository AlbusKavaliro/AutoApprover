using AutoApprover.Worker.Models;

namespace AutoApprover.Worker.Services;

internal interface IAuditRepository
{
    Task EnsureSchemaAsync(CancellationToken ct = default);
    Task WriteAsync(DecisionResult decision, CancellationToken ct = default);
}
