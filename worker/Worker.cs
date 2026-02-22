using AzCostPilot.Data;
using Microsoft.EntityFrameworkCore;

namespace AzCostPilot.Worker;

public class Worker(ILogger<Worker> logger, IDbContextFactory<AppDbContext> dbContextFactory) : BackgroundService
{
    private readonly ILogger<Worker> _logger = logger;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory = dbContextFactory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync(stoppingToken);
                var canConnect = await db.Database.CanConnectAsync(stoppingToken);
                _logger.LogInformation("Worker heartbeat at {time}. DB connected: {connected}", DateTimeOffset.UtcNow, canConnect);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker heartbeat failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
