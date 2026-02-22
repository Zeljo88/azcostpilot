namespace AzCostPilot.Api.Contracts;

public sealed record DashboardSummaryResponse(
    DateOnly Date,
    decimal YesterdayTotal,
    decimal TodayTotal,
    decimal Difference,
    decimal Baseline,
    bool SpikeFlag,
    DashboardCauseResourceResponse? TopCauseResource,
    string SuggestionText);

public sealed record DashboardCauseResourceResponse(
    string ResourceId,
    string ResourceName,
    string ResourceType,
    decimal IncreaseAmount);
