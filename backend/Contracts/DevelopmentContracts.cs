namespace AzCostPilot.Api.Contracts;

public sealed record SeedSyntheticCostDataRequest(
    string Scenario,
    int Days = 30,
    bool ClearExistingData = true,
    int? Seed = null);

public sealed record SeedSyntheticCostDataResponse(
    string Scenario,
    int Days,
    int DailyCostRowsInserted,
    int WasteFindingsInserted,
    int EventsGenerated,
    DateOnly FromDate,
    DateOnly ToDate,
    string Note);
