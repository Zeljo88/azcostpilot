namespace AzCostPilot.Api.Services;

public interface IAzureSubscriptionDiscoveryService
{
    Task<IReadOnlyList<AzureSubscriptionInfo>> ListSubscriptionsAsync(
        string tenantId,
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken);
}

public sealed record AzureSubscriptionInfo(string SubscriptionId, string DisplayName, string State);
