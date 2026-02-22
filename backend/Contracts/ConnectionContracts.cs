namespace AzCostPilot.Api.Contracts;

public sealed record SaveAzureConnectionRequest(string TenantId, string ClientId, string ClientSecret);

public sealed record AzureConnectionSummaryResponse(Guid Id, string TenantId, string ClientId, DateTime CreatedAtUtc);

public sealed record ConnectAzureResponse(
    bool Connected,
    Guid ConnectionId,
    int SubscriptionCount,
    List<ConnectedSubscriptionResponse> Subscriptions,
    bool BackfillCompleted,
    string BackfillMessage);

public sealed record ConnectedSubscriptionResponse(string SubscriptionId, string DisplayName, string State);
