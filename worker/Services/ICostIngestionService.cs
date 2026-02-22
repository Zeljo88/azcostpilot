namespace AzCostPilot.Worker.Services;

public interface ICostIngestionService
{
    Task<int> IngestLast7DaysAsync(CancellationToken cancellationToken);
}
