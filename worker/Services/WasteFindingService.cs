using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzCostPilot.Data;
using AzCostPilot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AzCostPilot.Worker.Services;

public sealed class WasteFindingService(
    HttpClient httpClient,
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISecretEncryptionService secretEncryptionService,
    ILogger<WasteFindingService> logger) : IWasteFindingService
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory = dbContextFactory;
    private readonly ISecretEncryptionService _secretEncryptionService = secretEncryptionService;
    private readonly ILogger<WasteFindingService> _logger = logger;

    public async Task<int> RefreshWasteFindingsAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var startDate = today.AddDays(-6);
        var vmActivityStartDate = today.AddDays(-30);
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var targets = await db.Subscriptions.AsNoTracking()
            .Join(
                db.AzureConnections.AsNoTracking(),
                subscription => subscription.AzureConnectionId,
                connection => connection.Id,
                (subscription, connection) => new ScanTarget(
                    subscription.UserId,
                    subscription.AzureSubscriptionId,
                    connection.TenantId,
                    connection.ClientId,
                    connection.EncryptedClientSecret))
            .ToListAsync(cancellationToken);
        var userIds = targets.Select(x => x.UserId).Distinct().ToList();
        var recentCostMap = await db.DailyCostResources.AsNoTracking()
            .Where(x => x.Date >= startDate && x.Date <= today)
            .GroupBy(x => new { x.UserId, x.ResourceId })
            .Select(group => new
            {
                group.Key.UserId,
                group.Key.ResourceId,
                SumCost = group.Sum(x => x.Cost)
            })
            .ToListAsync(cancellationToken);
        var estimatesByUser = recentCostMap
            .GroupBy(x => x.UserId)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(
                    item => NormalizeResourceId(item.ResourceId),
                    item => decimal.Round(item.SumCost * (30m / 7m), 4),
                    StringComparer.OrdinalIgnoreCase));
        var vmActivityRows = await db.DailyCostResources.AsNoTracking()
            .Where(x => userIds.Contains(x.UserId) && x.Date >= vmActivityStartDate && x.Date <= today)
            .Select(x => new
            {
                x.UserId,
                x.ResourceId,
                x.Date,
                x.Cost
            })
            .ToListAsync(cancellationToken);
        var vmActivityByResource = vmActivityRows
            .GroupBy(x => (x.UserId, ResourceId: NormalizeResourceId(x.ResourceId)))
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var activeDates = group
                        .Where(x => x.Cost > 0m)
                        .Select(x => x.Date)
                        .Distinct()
                        .OrderBy(x => x.DayNumber)
                        .ToList();
                    return new VmActivityProfile(
                        LastCostActiveDate: activeDates.Count == 0 ? null : activeDates[^1],
                        ActiveDates: activeDates);
                });
        var previousStoppedVmFindings = await db.WasteFindings.AsNoTracking()
            .Where(x => userIds.Contains(x.UserId) && x.FindingType == WasteFindingType.StoppedVm)
            .ToListAsync(cancellationToken);
        var previousLastSeenActiveByResource = previousStoppedVmFindings
            .Where(x => x.LastSeenActiveUtc is not null)
            .GroupBy(x => (x.UserId, ResourceId: NormalizeResourceId(x.ResourceId)))
            .ToDictionary(
                group => group.Key,
                group => group
                    .Where(x => x.LastSeenActiveUtc is not null)
                    .Max(x => x.LastSeenActiveUtc!.Value));
        var previousDetectedAtByResource = previousStoppedVmFindings
            .GroupBy(x => (x.UserId, ResourceId: NormalizeResourceId(x.ResourceId)))
            .ToDictionary(
                group => group.Key,
                group => group.Max(x => x.DetectedAtUtc));
        var collected = new List<WasteFindingCandidate>();

        foreach (var target in targets)
        {
            try
            {
                var clientSecret = _secretEncryptionService.Decrypt(target.EncryptedClientSecret);
                var accessToken = await GetAccessTokenAsync(
                    target.TenantId,
                    target.ClientId,
                    clientSecret,
                    cancellationToken);
                var disks = await QueryUnattachedDisksAsync(target, accessToken, cancellationToken);
                var publicIps = await QueryUnusedPublicIpsAsync(target, accessToken, cancellationToken);
                var stoppedVms = await QueryStoppedVmsAsync(
                    target,
                    accessToken,
                    today,
                    vmActivityByResource,
                    previousLastSeenActiveByResource,
                    previousDetectedAtByResource,
                    cancellationToken);

                collected.AddRange(disks);
                collected.AddRange(publicIps);
                collected.AddRange(stoppedVms);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Waste scan failed for subscription {SubscriptionId}.", target.AzureSubscriptionId);
            }
        }

        var now = DateTime.UtcNow;
        var findingTypes = new[]
        {
            WasteFindingType.UnattachedDisk,
            WasteFindingType.UnusedPublicIp,
            WasteFindingType.StoppedVm
        };
        if (userIds.Count > 0)
        {
            var previous = await db.WasteFindings
                .Where(x => userIds.Contains(x.UserId) && findingTypes.Contains(x.FindingType))
                .ToListAsync(cancellationToken);
            db.WasteFindings.RemoveRange(previous);
        }

        var newRows = collected
            .Select(candidate =>
            {
                var estimate = ResolveEstimate(candidate, estimatesByUser);
                return new WasteFinding
                {
                    Id = Guid.NewGuid(),
                    UserId = candidate.UserId,
                    AzureSubscriptionId = candidate.AzureSubscriptionId,
                    FindingType = candidate.FindingType,
                    ResourceId = Truncate(candidate.ResourceId, 1024),
                    ResourceName = Truncate(candidate.ResourceName, 256),
                    EstimatedMonthlyCost = estimate,
                    Classification = candidate.Classification,
                    InactiveDurationDays = candidate.InactiveDurationDays,
                    WasteConfidenceLevel = candidate.WasteConfidenceLevel,
                    LastSeenActiveUtc = candidate.LastSeenActiveUtc,
                    Status = "Open",
                    DetectedAtUtc = now
                };
            })
            .ToList();
        db.WasteFindings.AddRange(newRows);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Waste scan completed. Findings inserted: {FindingCount} across {SubscriptionCount} subscription(s).",
            newRows.Count,
            targets.Count);
        return newRows.Count;
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

    private async Task<List<WasteFindingCandidate>> QueryUnattachedDisksAsync(
        ScanTarget target,
        string accessToken,
        CancellationToken cancellationToken)
    {
        const string query = """
            Resources
            | where type =~ 'microsoft.compute/disks'
            | where isempty(tostring(managedBy))
            | project id, name, subscriptionId, sku = tostring(sku.name), sizeGb = toint(properties.diskSizeGB)
            """;
        var rows = await QueryResourceGraphAsync(target.AzureSubscriptionId, accessToken, query, cancellationToken);
        var findings = new List<WasteFindingCandidate>();
        foreach (var row in rows)
        {
            var resourceId = GetString(row, "id");
            if (string.IsNullOrWhiteSpace(resourceId))
            {
                continue;
            }

            var resourceName = GetString(row, "name");
            var sku = GetString(row, "sku");
            var sizeGb = GetInt(row, "sizeGb");
            findings.Add(new WasteFindingCandidate(
                target.UserId,
                target.AzureSubscriptionId,
                WasteFindingType.UnattachedDisk,
                resourceId,
                string.IsNullOrWhiteSpace(resourceName) ? ParseResourceName(resourceId) : resourceName,
                EstimateUnattachedDisk(sizeGb, sku)));
        }

        return findings;
    }

    private async Task<List<WasteFindingCandidate>> QueryUnusedPublicIpsAsync(
        ScanTarget target,
        string accessToken,
        CancellationToken cancellationToken)
    {
        const string query = """
            Resources
            | where type =~ 'microsoft.network/publicipaddresses'
            | extend ipConfigId = tostring(properties.ipConfiguration.id)
            | extend natGatewayId = tostring(properties.natGateway.id)
            | where isempty(ipConfigId) and isempty(natGatewayId)
            | project id, name, subscriptionId, sku = tostring(sku.name), allocation = tostring(properties.publicIPAllocationMethod)
            """;
        var rows = await QueryResourceGraphAsync(target.AzureSubscriptionId, accessToken, query, cancellationToken);
        var findings = new List<WasteFindingCandidate>();
        foreach (var row in rows)
        {
            var resourceId = GetString(row, "id");
            if (string.IsNullOrWhiteSpace(resourceId))
            {
                continue;
            }

            var resourceName = GetString(row, "name");
            var sku = GetString(row, "sku");
            var allocation = GetString(row, "allocation");
            findings.Add(new WasteFindingCandidate(
                target.UserId,
                target.AzureSubscriptionId,
                WasteFindingType.UnusedPublicIp,
                resourceId,
                string.IsNullOrWhiteSpace(resourceName) ? ParseResourceName(resourceId) : resourceName,
                EstimateUnusedPublicIp(sku, allocation)));
        }

        return findings;
    }

    private async Task<List<WasteFindingCandidate>> QueryStoppedVmsAsync(
        ScanTarget target,
        string accessToken,
        DateOnly today,
        Dictionary<(Guid UserId, string ResourceId), VmActivityProfile> vmActivityByResource,
        Dictionary<(Guid UserId, string ResourceId), DateTime> previousLastSeenActiveByResource,
        Dictionary<(Guid UserId, string ResourceId), DateTime> previousDetectedAtByResource,
        CancellationToken cancellationToken)
    {
        const string query = """
            Resources
            | where type =~ 'microsoft.compute/virtualmachines'
            | extend powerState = tostring(properties.extended.instanceView.powerState.code)
            | where powerState has 'stopped' or powerState has 'deallocated'
            | project id, name, subscriptionId, powerState
            """;
        var rows = await QueryResourceGraphAsync(target.AzureSubscriptionId, accessToken, query, cancellationToken);
        var findings = new List<WasteFindingCandidate>();
        foreach (var row in rows)
        {
            var resourceId = GetString(row, "id");
            if (string.IsNullOrWhiteSpace(resourceId))
            {
                continue;
            }

            var resourceName = GetString(row, "name");
            var resourceKey = (target.UserId, NormalizeResourceId(resourceId));
            vmActivityByResource.TryGetValue(resourceKey, out var profile);
            previousLastSeenActiveByResource.TryGetValue(resourceKey, out var previousLastSeenActiveUtc);
            previousDetectedAtByResource.TryGetValue(resourceKey, out var previousDetectedAtUtc);
            var lastCostActiveDate = profile?.LastCostActiveDate;
            var lastSeenActiveUtc = lastCostActiveDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            if (lastSeenActiveUtc is null && previousLastSeenActiveUtc != default)
            {
                lastSeenActiveUtc = previousLastSeenActiveUtc;
            }

            var inactiveDurationDays = ResolveInactiveDurationDays(today, lastCostActiveDate, lastSeenActiveUtc);
            var hasStrongHistorySignal = lastCostActiveDate is not null || lastSeenActiveUtc is not null;
            if (inactiveDurationDays is null && previousDetectedAtUtc != default)
            {
                var firstObservedStoppedDate = DateOnly.FromDateTime(previousDetectedAtUtc.ToUniversalTime().Date);
                inactiveDurationDays = today.DayNumber - firstObservedStoppedDate.DayNumber;
            }

            if (inactiveDurationDays is null || inactiveDurationDays.Value < 2m)
            {
                continue;
            }

            if (IsRepeatingStopStartPattern(profile, today))
            {
                continue;
            }

            var (classification, confidence) = ClassifyVmInactivity(inactiveDurationDays.Value, hasStrongHistorySignal);
            findings.Add(new WasteFindingCandidate(
                target.UserId,
                target.AzureSubscriptionId,
                WasteFindingType.StoppedVm,
                resourceId,
                string.IsNullOrWhiteSpace(resourceName) ? ParseResourceName(resourceId) : resourceName,
                EstimateStoppedVm(),
                classification,
                inactiveDurationDays,
                confidence,
                lastSeenActiveUtc));
        }

        return findings;
    }

    private async Task<List<JsonElement>> QueryResourceGraphAsync(
        string subscriptionId,
        string accessToken,
        string query,
        CancellationToken cancellationToken)
    {
        const string endpoint = "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01";
        var body = new
        {
            subscriptions = new[] { subscriptionId },
            query,
            options = new
            {
                resultFormat = "objectArray",
                top = 1000
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
                $"Resource Graph query failed ({(int)response.StatusCode}) for subscription {subscriptionId}. {Truncate(errorBody, 260)}");
        }

        var payload = await response.Content.ReadFromJsonAsync<ResourceGraphResponse>(cancellationToken: cancellationToken);
        if (payload?.Data.ValueKind == JsonValueKind.Array)
        {
            var list = new List<JsonElement>();
            foreach (var item in payload.Data.EnumerateArray())
            {
                list.Add(item.Clone());
            }

            return list;
        }

        return [];
    }

    private static decimal? ResolveEstimate(
        WasteFindingCandidate candidate,
        Dictionary<Guid, Dictionary<string, decimal>> estimatesByUser)
    {
        if (estimatesByUser.TryGetValue(candidate.UserId, out var byResource)
            && byResource.TryGetValue(NormalizeResourceId(candidate.ResourceId), out var estimateFromCost)
            && estimateFromCost > 0m)
        {
            return decimal.Round(estimateFromCost, 2);
        }

        return candidate.HeuristicEstimate is null
            ? null
            : decimal.Round(candidate.HeuristicEstimate.Value, 2);
    }

    private static decimal EstimateUnattachedDisk(int? sizeGb, string? sku)
    {
        var gb = sizeGb.GetValueOrDefault(0);
        if (gb <= 0)
        {
            return 10m;
        }

        var normalizedSku = (sku ?? string.Empty).ToLowerInvariant();
        var ratePerGb = normalizedSku switch
        {
            var value when value.Contains("premium") => 0.15m,
            var value when value.Contains("standardssd") || value.Contains("standard_ssd") => 0.08m,
            var value when value.Contains("standard") => 0.05m,
            _ => 0.07m
        };

        return gb * ratePerGb;
    }

    private static decimal EstimateUnusedPublicIp(string? sku, string? allocation)
    {
        var normalizedSku = (sku ?? string.Empty).ToLowerInvariant();
        var normalizedAllocation = (allocation ?? string.Empty).ToLowerInvariant();
        if (normalizedSku.Contains("standard"))
        {
            return 3.5m;
        }

        return normalizedAllocation.Contains("static") ? 2.5m : 2m;
    }

    private static decimal EstimateStoppedVm()
    {
        return 20m;
    }

    private static decimal? ResolveInactiveDurationDays(
        DateOnly today,
        DateOnly? lastCostActiveDate,
        DateTime? lastSeenActiveUtc)
    {
        if (lastCostActiveDate is not null)
        {
            return today.DayNumber - lastCostActiveDate.Value.DayNumber;
        }

        if (lastSeenActiveUtc is not null)
        {
            var lastSeenDate = DateOnly.FromDateTime(lastSeenActiveUtc.Value.ToUniversalTime().Date);
            return today.DayNumber - lastSeenDate.DayNumber;
        }

        return null;
    }

    private static bool IsRepeatingStopStartPattern(VmActivityProfile? profile, DateOnly today)
    {
        if (profile is null || profile.ActiveDates.Count == 0)
        {
            return false;
        }

        var lookbackStart = today.AddDays(-13).DayNumber;
        var activeDaysInLast14 = profile.ActiveDates.Count(x => x.DayNumber >= lookbackStart);
        return activeDaysInLast14 >= 6;
    }

    private static (string Classification, string Confidence) ClassifyVmInactivity(decimal inactiveDays, bool hasStrongHistorySignal)
    {
        if (!hasStrongHistorySignal)
        {
            return ("Possibly unused", "Low");
        }

        if (inactiveDays > 7m)
        {
            return ("Likely unused", "High");
        }

        return ("Possibly unused", "Medium");
    }

    private static string ParseResourceName(string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return "unknown";
        }

        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? "unknown" : parts[^1];
    }

    private static string NormalizeResourceId(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            return numeric;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);

    private sealed record ResourceGraphResponse(JsonElement Data);

    private sealed record ScanTarget(
        Guid UserId,
        string AzureSubscriptionId,
        string TenantId,
        string ClientId,
        string EncryptedClientSecret);

    private sealed record WasteFindingCandidate(
        Guid UserId,
        string AzureSubscriptionId,
        string FindingType,
        string ResourceId,
        string ResourceName,
        decimal? HeuristicEstimate,
        string? Classification = null,
        decimal? InactiveDurationDays = null,
        string? WasteConfidenceLevel = null,
        DateTime? LastSeenActiveUtc = null);

    private sealed record VmActivityProfile(
        DateOnly? LastCostActiveDate,
        List<DateOnly> ActiveDates);

    private static class WasteFindingType
    {
        public const string UnattachedDisk = "unattached_disk";
        public const string UnusedPublicIp = "unused_public_ip";
        public const string StoppedVm = "stopped_vm";
    }
}
