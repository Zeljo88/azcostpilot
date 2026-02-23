namespace AzCostPilot.Api.Contracts;

public sealed record DashboardSummaryResponse(
    DateOnly Date,
    DateTime LatestDataDate,
    decimal YesterdayTotal,
    decimal TodayTotal,
    decimal Difference,
    decimal Baseline,
    decimal MonthToDateTotal,
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
    string? TopResourceName,
    decimal? TopIncreaseAmount);

public sealed record DashboardWasteFindingResponse(
    string FindingType,
    string ResourceId,
    string ResourceName,
    string AzureSubscriptionId,
    decimal? EstimatedMonthlyCost,
    string? Classification,
    decimal? InactiveDurationDays,
    string? WasteConfidenceLevel,
    DateTime DetectedAtUtc,
    string Status);
