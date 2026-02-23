namespace AzCostPilot.Data.Entities;

public sealed class WasteFinding
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string AzureSubscriptionId { get; set; } = string.Empty;

    public string FindingType { get; set; } = string.Empty;

    public string ResourceId { get; set; } = string.Empty;

    public string ResourceName { get; set; } = string.Empty;

    public decimal? EstimatedMonthlyCost { get; set; }

    public string? Classification { get; set; }

    public decimal? InactiveDurationDays { get; set; }

    public string? WasteConfidenceLevel { get; set; }

    public DateTime? LastSeenActiveUtc { get; set; }

    public string Status { get; set; } = "Open";

    public DateTime DetectedAtUtc { get; set; }
}
