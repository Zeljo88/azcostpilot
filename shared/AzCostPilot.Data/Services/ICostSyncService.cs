namespace AzCostPilot.Data.Services;

public interface ICostSyncService
{
    Task<int> SyncCostsAsync(int days, Guid? userId, CancellationToken cancellationToken);

    Task<int> GenerateCostEventsAsync(int baselineDays, Guid? userId, CancellationToken cancellationToken);

    Task<BackfillResult> RunBackfillAsync(Guid userId, int costDays, CancellationToken cancellationToken);
}

public sealed record BackfillResult(
    int SubscriptionsProcessed,
    int EventsGenerated,
    DateOnly FromDate,
    DateOnly ToDate);
