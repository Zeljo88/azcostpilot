namespace AzCostPilot.Data.Entities;

public sealed class Subscription
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid AzureConnectionId { get; set; }

    public string AzureSubscriptionId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public User User { get; set; } = null!;

    public AzureConnection AzureConnection { get; set; } = null!;
}
