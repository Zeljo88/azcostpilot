using AzCostPilot.Worker.Services;

namespace AzCostPilot.Worker;

public class Worker(
    ILogger<Worker> logger,
    ICostIngestionService costIngestionService,
    ICostEventDetectionService costEventDetectionService,
    IWasteFindingService wasteFindingService,
    IConfiguration configuration) : BackgroundService
{
    private readonly ILogger<Worker> _logger = logger;
    private readonly ICostIngestionService _costIngestionService = costIngestionService;
    private readonly ICostEventDetectionService _costEventDetectionService = costEventDetectionService;
    private readonly IWasteFindingService _wasteFindingService = wasteFindingService;
    private readonly TimeSpan _runInterval = TimeSpan.FromHours(Math.Max(1, configuration.GetValue<int?>("Worker:RunIntervalHours") ?? 24));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cost worker started. Run interval: {Hours} hour(s).", _runInterval.TotalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processedSubscriptions = await _costIngestionService.IngestLast7DaysAsync(stoppingToken);
                var generatedEvents = await _costEventDetectionService.GenerateDailyEventsAsync(stoppingToken);
                var wasteFindings = await _wasteFindingService.RefreshWasteFindingsAsync(stoppingToken);
                _logger.LogInformation(
                    "Worker run complete. Subscriptions processed: {Subscriptions}. Cost events generated: {Events}. Waste findings: {WasteFindings}.",
                    processedSubscriptions,
                    generatedEvents,
                    wasteFindings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker ingestion run failed.");
            }

            await Task.Delay(_runInterval, stoppingToken);
        }
    }
}
