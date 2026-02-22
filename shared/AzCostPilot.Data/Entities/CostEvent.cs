namespace AzCostPilot.Data.Entities;

public sealed class CostEvent
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public DateOnly Date { get; set; }

    public decimal TotalYesterday { get; set; }

    public decimal TotalToday { get; set; }

    public decimal Difference { get; set; }

    public decimal Baseline { get; set; }

    public bool SpikeFlag { get; set; }

    public string? TopResourceId { get; set; }

    public string? TopResourceName { get; set; }

    public string? TopResourceType { get; set; }

    public decimal? TopIncreaseAmount { get; set; }

    public string? SuggestionText { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
