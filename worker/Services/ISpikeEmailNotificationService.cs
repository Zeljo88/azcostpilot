namespace AzCostPilot.Worker.Services;

public interface ISpikeEmailNotificationService
{
    Task<int> NotifyLatestSpikeAsync(CancellationToken cancellationToken);
}
