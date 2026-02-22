namespace AzCostPilot.Worker.Services;

public interface ICostEventDetectionService
{
    Task<int> GenerateDailyEventsAsync(CancellationToken cancellationToken);
}
