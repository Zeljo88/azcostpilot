namespace AzCostPilot.Data.Entities;

public sealed class User
{
    public Guid Id { get; set; }

    public string Email { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public List<AzureConnection> AzureConnections { get; set; } = [];
}
