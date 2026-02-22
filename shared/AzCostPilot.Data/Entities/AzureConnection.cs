namespace AzCostPilot.Data.Entities;

public sealed class AzureConnection
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string EncryptedClientSecret { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public User User { get; set; } = null!;

    public List<Subscription> Subscriptions { get; set; } = [];
}
