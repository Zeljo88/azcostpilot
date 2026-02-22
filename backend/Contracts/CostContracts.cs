namespace AzCostPilot.Api.Contracts;

public sealed record Latest7DaysCostResponse(
    DateOnly FromDate,
    DateOnly ToDate,
    decimal TotalCost,
    string Currency,
    List<Latest7DaysDailyTotalResponse> DailyTotals,
    List<Latest7DaysResourceCostResponse> Resources);

public sealed record Latest7DaysDailyTotalResponse(DateOnly Date, decimal Cost, string Currency);

public sealed record Latest7DaysResourceCostResponse(
    string ResourceId,
    decimal TotalCost,
    string Currency,
    List<Latest7DaysResourceDailyCostResponse> DailyCosts);

public sealed record Latest7DaysResourceDailyCostResponse(DateOnly Date, decimal Cost);
