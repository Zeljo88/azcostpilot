namespace AzCostPilot.Api.Services;

public interface IDevelopmentScenarioSeeder
{
    Task<DevelopmentScenarioSeedResult> SeedAsync(
        Guid userId,
        string scenario,
        int days,
        bool clearExistingData,
        int? seed,
        CancellationToken cancellationToken);
}

public sealed record DevelopmentScenarioSeedResult(
    string Scenario,
    int Days,
    int DailyCostRowsInserted,
    int WasteFindingsInserted,
    int EventsGenerated,
    DateOnly FromDate,
    DateOnly ToDate,
    string Note);
