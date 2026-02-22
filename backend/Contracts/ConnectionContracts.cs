namespace AzCostPilot.Api.Contracts;

public sealed record SaveAzureConnectionRequest(string TenantId, string ClientId, string ClientSecret);

public sealed record AzureConnectionSummaryResponse(Guid Id, string TenantId, string ClientId, DateTime CreatedAtUtc);
