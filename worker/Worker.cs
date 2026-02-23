using AzCostPilot.Data.Services;
using AzCostPilot.Worker.Services;

namespace AzCostPilot.Worker;

public class Worker(
    ILogger<Worker> logger,
    ICostSyncService costSyncService,
    IWasteFindingService wasteFindingService,
    ISpikeEmailNotificationService spikeEmailNotificationService,
    IConfiguration configuration) : BackgroundService
{
    private readonly ILogger<Worker> _logger = logger;
    private readonly ICostSyncService _costSyncService = costSyncService;
    private readonly IWasteFindingService _wasteFindingService = wasteFindingService;
    private readonly ISpikeEmailNotificationService _spikeEmailNotificationService = spikeEmailNotificationService;
    private readonly TimeSpan _runInterval = TimeSpan.FromHours(Math.Max(1, configuration.GetValue<int?>("Worker:RunIntervalHours") ?? 24));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cost worker started. Run interval: {Hours} hour(s).", _runInterval.TotalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processedSubscriptions = await _costSyncService.SyncCostsAsync(7, null, stoppingToken);
                var generatedEvents = await _costSyncService.GenerateCostEventsAsync(7, null, stoppingToken);
                var wasteFindings = await _wasteFindingService.RefreshWasteFindingsAsync(stoppingToken);
                var notificationsSent = await _spikeEmailNotificationService.NotifyLatestSpikeAsync(stoppingToken);
                _logger.LogInformation(
                    "Worker run complete. Subscriptions processed: {Subscriptions}. Cost events generated: {Events}. Waste findings: {WasteFindings}. Notifications sent: {NotificationsSent}.",
                    processedSubscriptions,
                    generatedEvents,
                    wasteFindings,
                    notificationsSent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker ingestion run failed.");
            }

            await Task.Delay(_runInterval, stoppingToken);
        }
    }
}
