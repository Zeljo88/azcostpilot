using AzCostPilot.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzCostPilot.Worker.Services;

public sealed class SpikeEmailNotificationService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IEmailSender emailSender,
    IOptions<NotificationOptions> options,
    ILogger<SpikeEmailNotificationService> logger) : ISpikeEmailNotificationService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory = dbContextFactory;
    private readonly IEmailSender _emailSender = emailSender;
    private readonly NotificationOptions _options = options.Value;
    private readonly ILogger<SpikeEmailNotificationService> _logger = logger;

    public async Task<int> NotifyLatestSpikeAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var latestCompleteDate = await GetLatestCompleteBillingDateAsync(db, cancellationToken);
        if (latestCompleteDate is null)
        {
            _logger.LogWarning("Email notifications skipped: no cost data found.");
            return 0;
        }

        var spikeRows = await (
            from eventRow in db.CostEvents
            join user in db.Users on eventRow.UserId equals user.Id
            where eventRow.Date == latestCompleteDate.Value && eventRow.SpikeFlag
            select new
            {
                user.Email,
                eventRow.Date,
                eventRow.TotalToday,
                eventRow.TotalYesterday,
                eventRow.Difference,
                eventRow.TopResourceName,
                eventRow.TopResourceType,
                eventRow.TopIncreaseAmount
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var row in spikeRows)
        {
            if (string.IsNullOrWhiteSpace(row.Email))
            {
                continue;
            }

            var subject = $"Azure Cost Spike Detected ({row.Date:yyyy-MM-dd})";
            var topResource = string.IsNullOrWhiteSpace(row.TopResourceName) ? "Unknown resource" : row.TopResourceName;
            var topType = string.IsNullOrWhiteSpace(row.TopResourceType) ? "unknown type" : row.TopResourceType;
            var increase = row.TopIncreaseAmount is null ? "n/a" : $"{row.TopIncreaseAmount.Value:F2} USD";
            var body = string.Join(Environment.NewLine,
            [
                $"A cost spike was detected for {row.Date:yyyy-MM-dd}.",
                $"Previous day: {row.TotalYesterday:F2} USD",
                $"Latest day: {row.TotalToday:F2} USD",
                $"Difference: {row.Difference:F2} USD",
                $"Top cause: {topResource} ({topType}), increase {increase}",
                "",
                "Azure Cost Spike Explainer"
            ]);

            try
            {
                await _emailSender.SendAsync(row.Email, subject, body, cancellationToken);
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send spike notification to {Email}.", row.Email);
            }
        }

        return sent;
    }

    private static async Task<DateOnly?> GetLatestCompleteBillingDateAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var currentDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var recentDates = await db.DailyCostResources.AsNoTracking()
            .Select(x => x.Date)
            .Distinct()
            .OrderByDescending(x => x)
            .Take(2)
            .ToListAsync(cancellationToken);

        if (recentDates.Count == 0)
        {
            return null;
        }

        var newestDate = recentDates[0];
        var mayStillBeProcessing = newestDate >= currentDate.AddDays(-1);
        if (mayStillBeProcessing && recentDates.Count > 1)
        {
            return recentDates[1];
        }

        return newestDate;
    }
}
