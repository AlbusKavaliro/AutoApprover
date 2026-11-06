using AutoApprover.Worker.Models;

namespace AutoApprover.Worker.Services;

public interface ITeamsNotifier
{
    Task NotifyAsync(DecisionResult decision);
}
