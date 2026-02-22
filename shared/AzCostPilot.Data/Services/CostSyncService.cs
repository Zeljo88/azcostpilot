using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzCostPilot.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzCostPilot.Data.Services;

public sealed class CostSyncService(
    HttpClient httpClient,
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISecretCipher secretCipher,
    ILogger<CostSyncService> logger) : ICostSyncService
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory = dbContextFactory;
    private readonly ISecretCipher _secretCipher = secretCipher;
    private readonly ILogger<CostSyncService> _logger = logger;

    public async Task<int> SyncCostsAsync(int days, Guid? userId, CancellationToken cancellationToken)
    {
        var windowDays = Math.Max(1, days);
        var endDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var startDate = endDate.AddDays(-(windowDays - 1));
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var subscriptions = db.Subscriptions.AsNoTracking();
        if (userId is not null)
        {
            subscriptions = subscriptions.Where(x => x.UserId == userId.Value);
        }

        var targetsQuery = subscriptions
            .Join(
                db.AzureConnections.AsNoTracking(),
                subscription => subscription.AzureConnectionId,
                connection => connection.Id,
                (subscription, connection) => new SyncTarget(
                    subscription.UserId,
                    subscription.AzureSubscriptionId,
                    connection.TenantId,
                    connection.ClientId,
                    connection.EncryptedClientSecret));

        var targets = await targetsQuery.ToListAsync(cancellationToken);
        var processed = 0;
        foreach (var target in targets)
        {
            try
            {
                var clientSecret = _secretCipher.Decrypt(target.EncryptedClientSecret);
                var accessToken = await GetAccessTokenAsync(target.TenantId, target.ClientId, clientSecret, cancellationToken);
                var costs = await QuerySubscriptionCostsAsync(
                    target.AzureSubscriptionId,
                    accessToken,
                    startDate,
                    endDate,
                    cancellationToken);

                var existingRows = await db.DailyCostResources
                    .Where(x => x.UserId == target.UserId
                        && x.AzureSubscriptionId == target.AzureSubscriptionId
                        && x.Date >= startDate
                        && x.Date <= endDate)
                    .ToListAsync(cancellationToken);
                db.DailyCostResources.RemoveRange(existingRows);

                var newRows = costs.Select(cost => new DailyCostResource
                {
                    UserId = target.UserId,
                    AzureSubscriptionId = target.AzureSubscriptionId,
                    Date = cost.Date,
                    ResourceId = cost.ResourceId.Length <= 1024 ? cost.ResourceId : cost.ResourceId[..1024],
                    Cost = cost.Cost,
                    Currency = cost.Currency
                });
                db.DailyCostResources.AddRange(newRows);
                await db.SaveChangesAsync(cancellationToken);
                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cost sync failed for subscription {SubscriptionId}.", target.AzureSubscriptionId);
            }
        }

        _logger.LogInformation(
            "Cost sync completed for {Processed} subscription(s) in window {StartDate}..{EndDate}.",
            processed,
            startDate,
            endDate);
        return processed;
    }

    public async Task<int> GenerateCostEventsAsync(int baselineDays, Guid? userId, CancellationToken cancellationToken)
    {
        var days = Math.Max(2, baselineDays);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var yesterday = today.AddDays(-1);
        var startDate = today.AddDays(-(days - 1));

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var userQuery = db.DailyCostResources.AsNoTracking()
            .Where(x => x.Date >= startDate && x.Date <= today);
        if (userId is not null)
        {
            userQuery = userQuery.Where(x => x.UserId == userId.Value);
        }

        var userIds = await userQuery
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var generated = 0;

        foreach (var currentUserId in userIds)
        {
            var rows = await db.DailyCostResources.AsNoTracking()
                .Where(x => x.UserId == currentUserId && x.Date >= startDate && x.Date <= today)
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
            var baselineValues = Enumerable.Range(1, days - 1)
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
                .Where(x => x.UserId == currentUserId && x.Date == today)
                .ToListAsync(cancellationToken);
            db.CostEvents.RemoveRange(existing);

            var eventRow = new CostEvent
            {
                Id = Guid.NewGuid(),
                UserId = currentUserId,
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

    public async Task<BackfillResult> RunBackfillAsync(Guid userId, int costDays, CancellationToken cancellationToken)
    {
        var days = Math.Max(7, costDays);
        var processed = await SyncCostsAsync(days, userId, cancellationToken);
        var generatedEvents = await GenerateCostEventsAsync(7, userId, cancellationToken);
        var endDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var startDate = endDate.AddDays(-(days - 1));
        return new BackfillResult(processed, generatedEvents, startDate, endDate);
    }

    private async Task<string> GetAccessTokenAsync(
        string tenantId,
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        var tokenEndpoint = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token";
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = "https://management.azure.com/.default"
        };

        using var response = await _httpClient.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Azure token request failed ({(int)response.StatusCode}) for tenant {tenantId}. {Truncate(errorBody, 240)}");
        }

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(token?.AccessToken))
        {
            throw new InvalidOperationException("Azure token request succeeded but returned no access token.");
        }

        return token.AccessToken;
    }

    private async Task<List<CostPoint>> QuerySubscriptionCostsAsync(
        string subscriptionId,
        string accessToken,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        var endpoint = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/query?api-version=2023-03-01";
        var body = new
        {
            type = "ActualCost",
            timeframe = "Custom",
            timePeriod = new
            {
                from = DateTime.SpecifyKind(startDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
                to = DateTime.SpecifyKind(endDate.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
            },
            dataset = new
            {
                granularity = "Daily",
                aggregation = new
                {
                    totalCost = new
                    {
                        name = "Cost",
                        function = "Sum"
                    }
                },
                grouping = new[]
                {
                    new
                    {
                        type = "Dimension",
                        name = "ResourceId"
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(body);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Cost query failed ({(int)response.StatusCode}) for subscription {subscriptionId}. {Truncate(errorBody, 240)}");
        }

        var payload = await response.Content.ReadFromJsonAsync<CostQueryResponse>(cancellationToken: cancellationToken);
        var columns = payload?.Properties?.Columns ?? [];
        var rows = payload?.Properties?.Rows ?? [];
        if (columns.Count == 0 || rows.Count == 0)
        {
            return [];
        }

        var indexes = columns
            .Select((column, index) => new { Name = column.Name ?? string.Empty, Index = index })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);
        if (!indexes.TryGetValue("UsageDate", out var usageDateIndex))
        {
            throw new InvalidOperationException("Cost query response did not contain UsageDate column.");
        }

        if (!indexes.TryGetValue("ResourceId", out var resourceIdIndex))
        {
            throw new InvalidOperationException("Cost query response did not contain ResourceId column.");
        }

        var costIndex = FindCostIndex(indexes);
        var currencyIndex = indexes.TryGetValue("Currency", out var idx) ? idx : -1;
        var aggregate = new Dictionary<(DateOnly Date, string ResourceId, string Currency), decimal>();
        foreach (var row in rows)
        {
            if (row.Count <= Math.Max(usageDateIndex, Math.Max(resourceIdIndex, costIndex)))
            {
                continue;
            }

            var date = ParseUsageDate(row[usageDateIndex]);
            var resourceId = ParseResourceId(row[resourceIdIndex]);
            var currency = currencyIndex >= 0 && currencyIndex < row.Count
                ? ParseCurrency(row[currencyIndex])
                : "USD";
            var cost = ParseDecimal(row[costIndex]);
            var key = (date, resourceId, currency);
            aggregate[key] = aggregate.TryGetValue(key, out var existing) ? existing + cost : cost;
        }

        return aggregate
            .Select(x => new CostPoint(x.Key.Date, x.Key.ResourceId, decimal.Round(x.Value, 4), x.Key.Currency))
            .OrderBy(x => x.Date)
            .ThenBy(x => x.ResourceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int FindCostIndex(Dictionary<string, int> indexes)
    {
        var candidates = new[]
        {
            "Cost",
            "PreTaxCost",
            "CostUSD",
            "CostInBillingCurrency"
        };

        foreach (var candidate in candidates)
        {
            if (indexes.TryGetValue(candidate, out var index))
            {
                return index;
            }
        }

        throw new InvalidOperationException("Cost query response did not contain a known cost column.");
    }

    private static DateOnly ParseUsageDate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numericDate))
        {
            return ParseNumericDate(numericDate);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidOperationException("UsageDate column returned an empty value.");
            }

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNumeric))
            {
                return ParseNumericDate(parsedNumeric);
            }

            if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                return parsedDate;
            }

            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDateTime))
            {
                return DateOnly.FromDateTime(parsedDateTime);
            }
        }

        throw new InvalidOperationException("UsageDate column returned an unsupported format.");
    }

    private static DateOnly ParseNumericDate(int yyyymmdd)
    {
        var year = yyyymmdd / 10000;
        var month = (yyyymmdd / 100) % 100;
        var day = yyyymmdd % 100;
        return new DateOnly(year, month, day);
    }

    private static string ParseResourceId(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "[unassigned]";
    }

    private static string ParseCurrency(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "USD";
    }

    private static decimal ParseDecimal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var numeric))
        {
            return numeric;
        }

        if (element.ValueKind == JsonValueKind.String
            && decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0m;
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

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "No details returned.";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : $"{trimmed[..maxLength]}...";
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);

    private sealed record CostQueryResponse(CostQueryProperties? Properties);

    private sealed record CostQueryProperties(List<CostQueryColumn> Columns, List<List<JsonElement>> Rows);

    private sealed record CostQueryColumn(string Name, string Type);

    private sealed record CostPoint(DateOnly Date, string ResourceId, decimal Cost, string Currency);

    private sealed record SyncTarget(
        Guid UserId,
        string AzureSubscriptionId,
        string TenantId,
        string ClientId,
        string EncryptedClientSecret);

    private sealed record ResourceDelta(string ResourceId, decimal Increase);
}
