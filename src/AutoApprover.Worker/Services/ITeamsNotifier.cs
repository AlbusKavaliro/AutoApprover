using AutoApprover.Worker.Models;

namespace AutoApprover.Worker.Services;

internal interface ITeamsNotifier
{
    Task NotifyAsync(DecisionResult decision);
}
