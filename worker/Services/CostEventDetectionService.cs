using AzCostPilot.Data;
using AzCostPilot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AzCostPilot.Worker.Services;

public sealed class CostEventDetectionService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ILogger<CostEventDetectionService> logger) : ICostEventDetectionService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory = dbContextFactory;
    private readonly ILogger<CostEventDetectionService> _logger = logger;

    public async Task<int> GenerateDailyEventsAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var yesterday = today.AddDays(-1);
        var startDate = today.AddDays(-6);

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var userIds = await db.DailyCostResources.AsNoTracking()
            .Where(x => x.Date >= startDate && x.Date <= today)
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var generated = 0;

        foreach (var userId in userIds)
        {
            var rows = await db.DailyCostResources.AsNoTracking()
                .Where(x => x.UserId == userId && x.Date >= startDate && x.Date <= today)
                .ToListAsync(cancellationToken);

            if (rows.Count == 0)
            {
                continue;
            }

            var dailyTotals = rows
                .GroupBy(x => x.Date)
                .ToDictionary(group => group.Key, group => group.Sum(x => x.Cost));
            var totalToday = dailyTotals.TryGetValue(today, out var todayValue) ? todayValue : 0m;
            var totalYesterday = dailyTotals.TryGetValue(yesterday, out var yesterdayValue) ? yesterdayValue : 0m;
            var baselineValues = Enumerable.Range(1, 6)
                .Select(offset => today.AddDays(-offset))
                .Where(date => dailyTotals.ContainsKey(date))
                .Select(date => dailyTotals[date])
                .ToList();

            var baseline = baselineValues.Count > 0
                ? baselineValues.Average()
                : dailyTotals.Values.Average();
            var difference = totalToday - totalYesterday;
            var spikeFlag = baseline > 0m
                && totalToday > baseline * 1.5m
                && difference > 5m;

            var todayByResource = rows
                .Where(x => x.Date == today)
                .GroupBy(x => x.ResourceId)
                .ToDictionary(group => group.Key, group => group.Sum(x => x.Cost), StringComparer.OrdinalIgnoreCase);
            var yesterdayByResource = rows
                .Where(x => x.Date == yesterday)
                .GroupBy(x => x.ResourceId)
                .ToDictionary(group => group.Key, group => group.Sum(x => x.Cost), StringComparer.OrdinalIgnoreCase);
            var allResources = todayByResource.Keys
                .Union(yesterdayByResource.Keys, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ResourceDelta? topDelta = null;
            foreach (var resourceId in allResources)
            {
                var todayCost = todayByResource.TryGetValue(resourceId, out var valueToday) ? valueToday : 0m;
                var yesterdayCost = yesterdayByResource.TryGetValue(resourceId, out var valueYesterday) ? valueYesterday : 0m;
                var delta = todayCost - yesterdayCost;
                if (delta <= 0m)
                {
                    continue;
                }

                if (topDelta is null || delta > topDelta.Increase)
                {
                    topDelta = new ResourceDelta(resourceId, delta);
                }
            }

            var existing = await db.CostEvents
                .Where(x => x.UserId == userId && x.Date == today)
                .ToListAsync(cancellationToken);
            db.CostEvents.RemoveRange(existing);

            var eventRow = new CostEvent
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Date = today,
                TotalYesterday = decimal.Round(totalYesterday, 4),
                TotalToday = decimal.Round(totalToday, 4),
                Difference = decimal.Round(difference, 4),
                Baseline = decimal.Round(baseline, 4),
                SpikeFlag = spikeFlag,
                TopResourceId = topDelta?.ResourceId,
                TopResourceName = topDelta is null ? null : ParseResourceName(topDelta.ResourceId),
                TopResourceType = topDelta is null ? null : ParseResourceType(topDelta.ResourceId),
                TopIncreaseAmount = topDelta is null ? null : decimal.Round(topDelta.Increase, 4),
                SuggestionText = BuildSuggestion(topDelta?.ResourceId, spikeFlag),
                CreatedAtUtc = DateTime.UtcNow
            };

            db.CostEvents.Add(eventRow);
            await db.SaveChangesAsync(cancellationToken);
            generated++;
        }

        _logger.LogInformation("Generated {Count} cost event row(s) for date {Date}.", generated, today);
        return generated;
    }

    private static string BuildSuggestion(string? resourceId, bool spikeFlag)
    {
        if (!spikeFlag)
        {
            return "No spike detected today.";
        }

        var resourceType = ParseResourceType(resourceId);
        if (resourceType.Contains("microsoft.compute/virtualmachines", StringComparison.OrdinalIgnoreCase))
        {
            return "VM cost increased. Check VM size, uptime schedule, and autoscaling settings.";
        }

        if (resourceType.Contains("microsoft.compute/disks", StringComparison.OrdinalIgnoreCase))
        {
            return "Disk cost increased. Check unattached disks and premium tier allocations.";
        }

        if (resourceType.Contains("microsoft.network/publicipaddresses", StringComparison.OrdinalIgnoreCase))
        {
            return "Public IP cost increased. Review unattached or idle public IPs.";
        }

        if (resourceType.Contains("microsoft.web/serverfarms", StringComparison.OrdinalIgnoreCase)
            || resourceType.Contains("microsoft.web/sites", StringComparison.OrdinalIgnoreCase))
        {
            return "App Service cost increased. Verify plan tier changes and scaling activity.";
        }

        return "Review this resource in Azure Cost Analysis and compare today versus yesterday usage.";
    }

    private static string ParseResourceName(string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return "Unknown Resource";
        }

        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? "Unknown Resource" : parts[^1];
    }

    private static string ParseResourceType(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return "unknown";
        }

        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var providerIndex = Array.FindIndex(parts, part => part.Equals("providers", StringComparison.OrdinalIgnoreCase));
        if (providerIndex < 0 || providerIndex + 1 >= parts.Length)
        {
            return "unknown";
        }

        var provider = parts[providerIndex + 1];
        var typeSegments = new List<string>();
        for (var index = providerIndex + 2; index < parts.Length; index += 2)
        {
            typeSegments.Add(parts[index]);
        }

        return typeSegments.Count == 0 ? provider : $"{provider}/{string.Join("/", typeSegments)}";
    }

    private sealed record ResourceDelta(string ResourceId, decimal Increase);
}
