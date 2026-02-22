namespace AzCostPilot.Api.Contracts;

public sealed record DashboardSummaryResponse(
    DateOnly Date,
    decimal YesterdayTotal,
    decimal TodayTotal,
    decimal Difference,
    decimal Baseline,
    bool SpikeFlag,
    string Confidence,
    DashboardCauseResourceResponse? TopCauseResource,
    string SuggestionText);

public sealed record DashboardCauseResourceResponse(
    string ResourceId,
    string ResourceName,
    string ResourceType,
    decimal IncreaseAmount);

public sealed record DashboardHistoryItemResponse(
    DateOnly Date,
    decimal YesterdayTotal,
    decimal TodayTotal,
    decimal Difference,
    bool SpikeFlag,
    string? TopResourceId,
    string? TopResourceName,
    decimal? TopIncreaseAmount,
    string SuggestionText);

public sealed record DashboardWasteFindingResponse(
    string FindingType,
    string ResourceId,
    string ResourceName,
    string AzureSubscriptionId,
    decimal? EstimatedMonthlyCost,
    DateTime DetectedAtUtc,
    string Status);
