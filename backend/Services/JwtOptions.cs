namespace AzCostPilot.Api.Services;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "AzCostPilot";

    public string Audience { get; set; } = "AzCostPilot.Client";

    public string Key { get; set; } = "local-dev-jwt-key-change-me-please";

    public int ExpiryMinutes { get; set; } = 120;
}
