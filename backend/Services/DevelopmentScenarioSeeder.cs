using AzCostPilot.Data;
using AzCostPilot.Data.Entities;
using AzCostPilot.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AzCostPilot.Api.Services;

public sealed class DevelopmentScenarioSeeder(
    AppDbContext db,
    ICostSyncService costSyncService) : IDevelopmentScenarioSeeder
{
    private const string DevFallbackSubscriptionId = "11111111-1111-1111-1111-111111111111";

    private static readonly ResourceTemplate[] ResourceTemplates =
    [
        new(
            Key: "vm",
            BaseDailyCost: 2.80m,
            Volatility: 0.08m,
            ResourceIdTemplate: "/subscriptions/{subscriptionId}/resourceGroups/azcost-app-rg/providers/Microsoft.Compute/virtualMachines/app-vm-01"),
        new(
            Key: "sql",
            BaseDailyCost: 3.90m,
            Volatility: 0.07m,
            ResourceIdTemplate: "/subscriptions/{subscriptionId}/resourceGroups/azcost-data-rg/providers/Microsoft.Sql/servers/sql-prod-01/databases/appdb"),
        new(
            Key: "appservice",
            BaseDailyCost: 1.45m,
            Volatility: 0.10m,
            ResourceIdTemplate: "/subscriptions/{subscriptionId}/resourceGroups/azcost-app-rg/providers/Microsoft.Web/sites/api-app-01"),
        new(
            Key: "storage",
            BaseDailyCost: 0.95m,
            Volatility: 0.06m,
            ResourceIdTemplate: "/subscriptions/{subscriptionId}/resourceGroups/azcost-storage-rg/providers/Microsoft.Storage/storageAccounts/appstorage01"),
        new(
            Key: "monitor",
            BaseDailyCost: 0.70m,
            Volatility: 0.12m,
            ResourceIdTemplate: "/subscriptions/{subscriptionId}/resourceGroups/azcost-monitor-rg/providers/Microsoft.OperationalInsights/workspaces/app-law"),
        new(
            Key: "publicip",
            BaseDailyCost: 0.18m,
            Volatility: 0.15m,
            ResourceIdTemplate: "/subscriptions/{subscriptionId}/resourceGroups/azcost-net-rg/providers/Microsoft.Network/publicIPAddresses/app-pip-01")
    ];

    public async Task<DevelopmentScenarioSeedResult> SeedAsync(
        Guid userId,
        string scenario,
        int days,
        bool clearExistingData,
        int? seed,
        CancellationToken cancellationToken)
    {
        var normalizedScenario = NormalizeScenario(scenario);
        var safeDays = Math.Clamp(days, 7, 60);
        var toDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var fromDate = toDate.AddDays(-(safeDays - 1));
        var random = seed is null ? new Random() : new Random(seed.Value);

        var subscriptionId = await db.Subscriptions.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => x.AzureSubscriptionId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? DevFallbackSubscriptionId;

        await RemoveExistingDataAsync(userId, fromDate, clearExistingData, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var costRows = BuildScenarioRows(userId, subscriptionId, normalizedScenario, fromDate, toDate, random);
        db.DailyCostResources.AddRange(costRows);

        var wasteFindings = BuildWasteFindings(userId, subscriptionId, normalizedScenario);
        if (wasteFindings.Count > 0)
        {
            db.WasteFindings.AddRange(wasteFindings);
        }

        await db.SaveChangesAsync(cancellationToken);

        var generatedEvents = await costSyncService.GenerateCostEventsAsync(7, userId, cancellationToken);

        return new DevelopmentScenarioSeedResult(
            Scenario: normalizedScenario,
            Days: safeDays,
            DailyCostRowsInserted: costRows.Count,
            WasteFindingsInserted: wasteFindings.Count,
            EventsGenerated: generatedEvents,
            FromDate: fromDate,
            ToDate: toDate,
            Note: BuildScenarioNote(normalizedScenario));
    }

    private async Task RemoveExistingDataAsync(
        Guid userId,
        DateOnly fromDate,
        bool clearExistingData,
        CancellationToken cancellationToken)
    {
        if (clearExistingData)
        {
            var allRows = await db.DailyCostResources.Where(x => x.UserId == userId).ToListAsync(cancellationToken);
            db.DailyCostResources.RemoveRange(allRows);

            var allEvents = await db.CostEvents.Where(x => x.UserId == userId).ToListAsync(cancellationToken);
            db.CostEvents.RemoveRange(allEvents);
        }
        else
        {
            var windowRows = await db.DailyCostResources
                .Where(x => x.UserId == userId && x.Date >= fromDate)
                .ToListAsync(cancellationToken);
            db.DailyCostResources.RemoveRange(windowRows);

            var windowEvents = await db.CostEvents
                .Where(x => x.UserId == userId && x.Date >= fromDate)
                .ToListAsync(cancellationToken);
            db.CostEvents.RemoveRange(windowEvents);
        }

        var findings = await db.WasteFindings.Where(x => x.UserId == userId).ToListAsync(cancellationToken);
        db.WasteFindings.RemoveRange(findings);
    }

    private static List<DailyCostResource> BuildScenarioRows(
        Guid userId,
        string subscriptionId,
        string scenario,
        DateOnly fromDate,
        DateOnly toDate,
        Random random)
    {
        var rows = new List<DailyCostResource>();
        var dayCount = toDate.DayNumber - fromDate.DayNumber + 1;

        for (var offset = 0; offset < dayCount; offset++)
        {
            var date = fromDate.AddDays(offset);
            foreach (var template in ResourceTemplates)
            {
                if (scenario == "missing_data" && ShouldSkipForMissingData(date, toDate, template.Key))
                {
                    continue;
                }

                var resourceId = template.ResourceIdTemplate.Replace("{subscriptionId}", subscriptionId, StringComparison.OrdinalIgnoreCase);
                var dailyCost = BuildScenarioCost(scenario, date, toDate, offset, dayCount, template, random);
                if (dailyCost <= 0m)
                {
                    continue;
                }

                rows.Add(new DailyCostResource
                {
                    UserId = userId,
                    AzureSubscriptionId = subscriptionId,
                    Date = date,
                    ResourceId = resourceId,
                    Cost = decimal.Round(dailyCost, 4),
                    Currency = "USD"
                });
            }
        }

        return rows;
    }

    private static decimal BuildScenarioCost(
        string scenario,
        DateOnly date,
        DateOnly toDate,
        int offset,
        int dayCount,
        ResourceTemplate template,
        Random random)
    {
        var weekFactor = GetWeekFactor(date.DayOfWeek);
        var noiseFactor = GetNoiseFactor(random, template.Volatility);
        var baseCost = template.BaseDailyCost * weekFactor * noiseFactor;

        return scenario switch
        {
            "normal" => baseCost,
            "spike" => BuildSpikeCost(baseCost, date, toDate, template, random),
            "noisy_increases" => BuildNoisyIncreaseCost(baseCost, date, toDate, template, random),
            "missing_data" => BuildMissingDataCost(baseCost, date, toDate, offset, dayCount),
            "idle_resources" => BuildIdleCost(template, random),
            _ => baseCost
        };
    }

    private static decimal BuildSpikeCost(
        decimal baseCost,
        DateOnly date,
        DateOnly toDate,
        ResourceTemplate template,
        Random random)
    {
        var latestCompleteDay = toDate.AddDays(-1);
        var secondarySpikeDay = toDate.AddDays(-4);
        if (date != latestCompleteDay && date != secondarySpikeDay)
        {
            return baseCost;
        }

        if (template.Key == "sql")
        {
            var multiplier = date == latestCompleteDay ? 4.8m : 3.1m;
            var additive = date == latestCompleteDay ? 15m : 7m;
            return baseCost * multiplier + additive;
        }

        if (template.Key == "monitor")
        {
            return date == latestCompleteDay ? baseCost * 1.7m : baseCost * 1.35m;
        }

        return baseCost * (1.05m + ((decimal)random.NextDouble() * 0.08m));
    }

    private static decimal BuildNoisyIncreaseCost(
        decimal baseCost,
        DateOnly date,
        DateOnly toDate,
        ResourceTemplate template,
        Random random)
    {
        _ = template;

        if (date == toDate)
        {
            return baseCost * (1.55m + ((decimal)random.NextDouble() * 0.2m));
        }

        if (date == toDate.AddDays(-1))
        {
            return baseCost * (1.25m + ((decimal)random.NextDouble() * 0.15m));
        }

        if (date >= toDate.AddDays(-3))
        {
            return baseCost * (1.08m + ((decimal)random.NextDouble() * 0.1m));
        }

        return baseCost;
    }

    private static decimal BuildMissingDataCost(
        decimal baseCost,
        DateOnly date,
        DateOnly toDate,
        int offset,
        int dayCount)
    {
        _ = dayCount;

        if (date == toDate)
        {
            return 0m;
        }

        if (offset % 11 == 0)
        {
            return baseCost * 0.85m;
        }

        return baseCost;
    }

    private static decimal BuildIdleCost(ResourceTemplate template, Random random)
    {
        var idleBase = template.Key switch
        {
            "vm" => 0.09m,
            "sql" => 0.05m,
            "appservice" => 0.04m,
            "storage" => 0.12m,
            "monitor" => 0.03m,
            "publicip" => 0.07m,
            _ => 0.02m
        };

        return idleBase * (0.8m + ((decimal)random.NextDouble() * 0.25m));
    }

    private static bool ShouldSkipForMissingData(DateOnly date, DateOnly toDate, string resourceKey)
    {
        if (date == toDate)
        {
            return true;
        }

        return resourceKey switch
        {
            "appservice" when date == toDate.AddDays(-3) => true,
            "storage" when date == toDate.AddDays(-8) => true,
            _ => false
        };
    }

    private static decimal GetWeekFactor(DayOfWeek dayOfWeek)
    {
        return dayOfWeek switch
        {
            DayOfWeek.Monday => 1.06m,
            DayOfWeek.Tuesday => 1.08m,
            DayOfWeek.Wednesday => 1.04m,
            DayOfWeek.Thursday => 1.03m,
            DayOfWeek.Friday => 0.97m,
            DayOfWeek.Saturday => 0.86m,
            DayOfWeek.Sunday => 0.83m,
            _ => 1m
        };
    }

    private static decimal GetNoiseFactor(Random random, decimal volatility)
    {
        var signed = ((decimal)random.NextDouble() * 2m) - 1m;
        return 1m + (signed * volatility);
    }

    private static List<WasteFinding> BuildWasteFindings(Guid userId, string subscriptionId, string scenario)
    {
        if (!string.Equals(scenario, "idle_resources", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var now = DateTime.UtcNow;
        return
        [
            new WasteFinding
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AzureSubscriptionId = subscriptionId,
                FindingType = "stopped_vm",
                ResourceId = $"/subscriptions/{subscriptionId}/resourceGroups/azcost-idle-rg/providers/Microsoft.Compute/virtualMachines/stopped-vm-01",
                ResourceName = "stopped-vm-01",
                EstimatedMonthlyCost = 14.80m,
                Status = "open",
                DetectedAtUtc = now.AddMinutes(-15)
            },
            new WasteFinding
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AzureSubscriptionId = subscriptionId,
                FindingType = "unattached_disk",
                ResourceId = $"/subscriptions/{subscriptionId}/resourceGroups/azcost-idle-rg/providers/Microsoft.Compute/disks/orphaned-disk-01",
                ResourceName = "orphaned-disk-01",
                EstimatedMonthlyCost = 8.40m,
                Status = "open",
                DetectedAtUtc = now.AddMinutes(-12)
            },
            new WasteFinding
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                AzureSubscriptionId = subscriptionId,
                FindingType = "unused_public_ip",
                ResourceId = $"/subscriptions/{subscriptionId}/resourceGroups/azcost-idle-rg/providers/Microsoft.Network/publicIPAddresses/unused-ip-01",
                ResourceName = "unused-ip-01",
                EstimatedMonthlyCost = 3.70m,
                Status = "open",
                DetectedAtUtc = now.AddMinutes(-10)
            }
        ];
    }

    private static string BuildScenarioNote(string scenario)
    {
        return scenario switch
        {
            "normal" => "Stable weekday/weekend pattern with normal variance.",
            "spike" => "Latest complete billing day has a sharp SQL cost increase to trigger spike detection.",
            "noisy_increases" => "Multiple resources increase together, producing a noisy upward trend.",
            "missing_data" => "Latest day is intentionally missing to simulate delayed or incomplete ingestion.",
            "idle_resources" => "Costs are near-zero and idle resource findings are created for savings tests.",
            _ => "Synthetic data generated."
        };
    }

    private static string NormalizeScenario(string scenario)
    {
        var normalized = scenario.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return normalized switch
        {
            "normal" => "normal",
            "spike" => "spike",
            "noisy" => "noisy_increases",
            "noisy_increases" => "noisy_increases",
            "missing" => "missing_data",
            "missing_data" => "missing_data",
            "idle" => "idle_resources",
            "idle_resources" => "idle_resources",
            _ => throw new ArgumentOutOfRangeException(
                nameof(scenario),
                "Scenario must be one of: normal, spike, noisy_increases, missing_data, idle_resources.")
        };
    }

    private sealed record ResourceTemplate(
        string Key,
        decimal BaseDailyCost,
        decimal Volatility,
        string ResourceIdTemplate);
}
