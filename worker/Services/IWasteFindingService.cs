namespace AzCostPilot.Worker.Services;

public interface IWasteFindingService
{
    Task<int> RefreshWasteFindingsAsync(CancellationToken cancellationToken);
}
