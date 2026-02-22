namespace AzCostPilot.Data.Entities;

public sealed class DailyCostResource
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public string AzureSubscriptionId { get; set; } = string.Empty;

    public DateOnly Date { get; set; }

    public string ResourceId { get; set; } = string.Empty;

    public decimal Cost { get; set; }

    public string Currency { get; set; } = "USD";
}
