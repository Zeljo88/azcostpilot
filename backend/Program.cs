using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AzCostPilot.Api.Contracts;
using AzCostPilot.Api.Services;
using AzCostPilot.Data;
using AzCostPilot.Data.Entities;
using AzCostPilot.Data.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var configuredAllowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<ISecretEncryptionService, SecretEncryptionService>();
builder.Services.AddScoped<ISecretCipher>(sp => sp.GetRequiredService<ISecretEncryptionService>());
builder.Services.AddScoped<IDevelopmentScenarioSeeder, DevelopmentScenarioSeeder>();
builder.Services.AddHttpClient<IAzureSubscriptionDiscoveryService, AzureSubscriptionDiscoveryService>();
builder.Services.AddHttpClient<ICostSyncService, CostSyncService>();

var jwtKey = builder.Configuration["Jwt:Key"] ?? "local-dev-jwt-key-change-me-please";
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "AzCostPilot",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "AzCostPilot.Client",
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "frontend",
        policy => policy.SetIsOriginAllowed(origin => IsAllowedFrontendOrigin(origin, configuredAllowedOrigins))
            .AllowAnyHeader()
            .AllowAnyMethod());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

await ApplyMigrationsAsync(app);

app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    utcNow = DateTime.UtcNow
}));

app.MapGet("/health/db", async (AppDbContext db, CancellationToken cancellationToken) =>
{
    var canConnect = await db.Database.CanConnectAsync(cancellationToken);
    return canConnect
        ? Results.Ok(new { status = "ok", database = "connected" })
        : Results.Problem("Database connection failed.", statusCode: StatusCodes.Status503ServiceUnavailable);
});

var auth = app.MapGroup("/auth");

auth.MapPost("/register", async (RegisterRequest request, AppDbContext db, CancellationToken cancellationToken) =>
{
    var email = request.Email.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { message = "Email and password are required." });
    }

    var exists = await db.Users.AnyAsync(x => x.Email == email, cancellationToken);
    if (exists)
    {
        return Results.Conflict(new { message = "User already exists." });
    }

    var user = new User
    {
        Id = Guid.NewGuid(),
        Email = email,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
        CreatedAtUtc = DateTime.UtcNow
    };

    db.Users.Add(user);
    await db.SaveChangesAsync(cancellationToken);

    return Results.Created($"/auth/users/{user.Id}", new
    {
        user.Id,
        user.Email
    });
});

auth.MapPost("/login", async (LoginRequest request, AppDbContext db, ITokenService tokenService, CancellationToken cancellationToken) =>
{
    var email = request.Email.Trim().ToLowerInvariant();
    var user = await db.Users.FirstOrDefaultAsync(x => x.Email == email, cancellationToken);
    if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
    {
        return Results.Unauthorized();
    }

    var token = tokenService.CreateToken(user);
    return Results.Ok(new AuthResponse(token, user.Email, user.Id));
});

app.MapGet("/auth/me", (ClaimsPrincipal principal) =>
{
    var userIdRaw = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
    var email = principal.FindFirstValue(JwtRegisteredClaimNames.Email)
        ?? principal.FindFirstValue(ClaimTypes.Email);
    if (string.IsNullOrWhiteSpace(userIdRaw) || !Guid.TryParse(userIdRaw, out var userId))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new { userId, email });
}).RequireAuthorization();

var connect = app.MapGroup("/connect").RequireAuthorization();

connect.MapPost("/azure", async (
    SaveAzureConnectionRequest request,
    ClaimsPrincipal principal,
    AppDbContext db,
    ISecretEncryptionService secretEncryptionService,
    IAzureSubscriptionDiscoveryService azureSubscriptionDiscoveryService,
    ICostSyncService costSyncService,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.TenantId) ||
        string.IsNullOrWhiteSpace(request.ClientId) ||
        string.IsNullOrWhiteSpace(request.ClientSecret))
    {
        return Results.BadRequest(new { message = "tenantId, clientId, and clientSecret are required." });
    }

    var tenantId = request.TenantId.Trim();
    var clientId = request.ClientId.Trim();
    var clientSecret = request.ClientSecret.Trim();

    IReadOnlyList<AzureSubscriptionInfo> discoveredSubscriptions;
    try
    {
        discoveredSubscriptions = await azureSubscriptionDiscoveryService.ListSubscriptionsAsync(
            tenantId,
            clientId,
            clientSecret,
            cancellationToken);
    }
    catch (AzureConnectionValidationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }

    var existing = await db.AzureConnections.FirstOrDefaultAsync(
        x => x.UserId == userId.Value && x.TenantId == tenantId && x.ClientId == clientId,
        cancellationToken);

    if (existing is null)
    {
        existing = new AzureConnection
        {
            Id = Guid.NewGuid(),
            UserId = userId.Value,
            TenantId = tenantId,
            ClientId = clientId,
            EncryptedClientSecret = secretEncryptionService.Encrypt(clientSecret),
            CreatedAtUtc = DateTime.UtcNow
        };
        db.AzureConnections.Add(existing);
    }
    else
    {
        existing.EncryptedClientSecret = secretEncryptionService.Encrypt(clientSecret);
    }

    var existingSubscriptions = await db.Subscriptions
        .Where(x => x.AzureConnectionId == existing.Id)
        .ToListAsync(cancellationToken);
    db.Subscriptions.RemoveRange(existingSubscriptions);

    var createdAtUtc = DateTime.UtcNow;
    var subscriptionRows = discoveredSubscriptions.Select(subscription => new Subscription
    {
        Id = Guid.NewGuid(),
        UserId = userId.Value,
        AzureConnectionId = existing.Id,
        AzureSubscriptionId = subscription.SubscriptionId,
        DisplayName = subscription.DisplayName,
        CreatedAtUtc = createdAtUtc
    });
    db.Subscriptions.AddRange(subscriptionRows);

    await db.SaveChangesAsync(cancellationToken);
    var logger = loggerFactory.CreateLogger("ConnectAzure");
    var backfillCompleted = true;
    var backfillMessage = "Initial cost sync completed.";
    try
    {
        var backfill = await costSyncService.RunBackfillAsync(userId.Value, 30, cancellationToken);
        backfillMessage =
            $"Initial cost sync completed for {backfill.SubscriptionsProcessed} subscription(s), {backfill.FromDate} to {backfill.ToDate}.";
    }
    catch (Exception ex)
    {
        backfillCompleted = false;
        backfillMessage = "Connected, but initial sync failed. Scheduled worker will retry.";
        logger.LogWarning(ex, "Backfill failed after Azure connect for user {UserId}.", userId.Value);
    }

    return Results.Ok(new ConnectAzureResponse(
        Connected: true,
        ConnectionId: existing.Id,
        SubscriptionCount: discoveredSubscriptions.Count,
        Subscriptions: discoveredSubscriptions
            .Select(x => new ConnectedSubscriptionResponse(x.SubscriptionId, x.DisplayName, x.State))
            .ToList(),
        BackfillCompleted: backfillCompleted,
        BackfillMessage: backfillMessage));
});

var connections = app.MapGroup("/connections").RequireAuthorization();

connections.MapGet("/azure", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var rows = await db.AzureConnections.AsNoTracking()
        .Where(x => x.UserId == userId.Value)
        .OrderByDescending(x => x.CreatedAtUtc)
        .Select(x => new AzureConnectionSummaryResponse(x.Id, x.TenantId, x.ClientId, x.CreatedAtUtc))
        .ToListAsync(cancellationToken);

    return Results.Ok(rows);
});

var cost = app.MapGroup("/cost").RequireAuthorization();

cost.MapGet("/latest-7-days", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var toDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);
    var fromDate = toDate.AddDays(-6);
    var rows = await db.DailyCostResources.AsNoTracking()
        .Where(x => x.UserId == userId.Value && x.Date >= fromDate && x.Date <= toDate)
        .OrderBy(x => x.Date)
        .ToListAsync(cancellationToken);

    var totalCurrency = ResolveCurrency(rows.Select(x => x.Currency));
    var dailyTotalsByDate = rows
        .GroupBy(x => x.Date)
        .ToDictionary(
            group => group.Key,
            group => new Latest7DaysDailyTotalResponse(
                group.Key,
                decimal.Round(group.Sum(x => x.Cost), 4),
                ResolveCurrency(group.Select(x => x.Currency))));
    var dailyTotals = Enumerable.Range(0, 7)
        .Select(offset =>
        {
            var date = fromDate.AddDays(offset);
            return dailyTotalsByDate.TryGetValue(date, out var existing)
                ? existing
                : new Latest7DaysDailyTotalResponse(date, 0m, totalCurrency);
        })
        .ToList();

    var resources = rows
        .GroupBy(x => x.ResourceId)
        .Select(group => new Latest7DaysResourceCostResponse(
            group.Key,
            decimal.Round(group.Sum(x => x.Cost), 4),
            ResolveCurrency(group.Select(x => x.Currency)),
            group.GroupBy(x => x.Date)
                .Select(dateGroup => new Latest7DaysResourceDailyCostResponse(
                    dateGroup.Key,
                    decimal.Round(dateGroup.Sum(x => x.Cost), 4)))
                .OrderBy(x => x.Date)
                .ToList()))
        .OrderByDescending(x => x.TotalCost)
        .ToList();

    var response = new Latest7DaysCostResponse(
        FromDate: fromDate,
        ToDate: toDate,
        TotalCost: decimal.Round(rows.Sum(x => x.Cost), 4),
        Currency: totalCurrency,
        DailyTotals: dailyTotals,
        Resources: resources);

    return Results.Ok(response);
});

var dashboard = app.MapGroup("/dashboard").RequireAuthorization();

dashboard.MapGet("/summary", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var currentDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);
    var monthStart = new DateOnly(currentDate.Year, currentDate.Month, 1);
    var monthToDateTotal = await db.DailyCostResources.AsNoTracking()
        .Where(x => x.UserId == userId.Value && x.Date >= monthStart && x.Date <= currentDate)
        .SumAsync(x => (decimal?)x.Cost, cancellationToken) ?? 0m;

    var latestDate = await GetLatestCompleteBillingDateAsync(
        db,
        userId.Value,
        currentDate,
        cancellationToken);

    if (latestDate is null)
    {
        var empty = new DashboardSummaryResponse(
            Date: currentDate,
            LatestDataDate: DateTime.SpecifyKind(currentDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
            YesterdayTotal: 0m,
            TodayTotal: 0m,
            Difference: 0m,
            Baseline: 0m,
            MonthToDateTotal: decimal.Round(monthToDateTotal, 4),
            SpikeFlag: false,
            Confidence: "Low",
            TopCauseResource: null,
            SuggestionText: "No cost event yet. Run worker ingestion to generate a daily summary.");
        return Results.Ok(empty);
    }

    var previousDate = latestDate.Value.AddDays(-1);
    var metricsRows = await db.DailyCostResources.AsNoTracking()
        .Where(x => x.UserId == userId.Value && x.Date <= latestDate.Value)
        .Select(x => new { x.Date, x.ResourceId, x.Cost })
        .ToListAsync(cancellationToken);

    var totalsByDate = metricsRows
        .GroupBy(x => x.Date)
        .ToDictionary(group => group.Key, group => group.Sum(x => x.Cost));
    var latestTotal = totalsByDate.TryGetValue(latestDate.Value, out var latestValue) ? latestValue : 0m;
    var previousTotal = totalsByDate.TryGetValue(previousDate, out var previousValue) ? previousValue : 0m;
    var difference = latestTotal - previousTotal;
    var baselineDates = totalsByDate.Keys
        .Where(x => x <= latestDate.Value)
        .OrderByDescending(x => x)
        .Take(7)
        .ToList();
    var baseline = baselineDates.Count > 0
        ? baselineDates.Average(x => totalsByDate[x])
        : 0m;
    var spikeFlag = baseline > 0m
        && latestTotal > baseline * 1.5m
        && difference > 5m;

    DashboardCauseResourceResponse? topCause = null;
    var latestByResource = metricsRows
        .Where(x => x.Date == latestDate.Value)
        .GroupBy(x => x.ResourceId)
        .ToDictionary(group => group.Key, group => group.Sum(x => x.Cost), StringComparer.OrdinalIgnoreCase);
    var previousByResource = metricsRows
        .Where(x => x.Date == previousDate)
        .GroupBy(x => x.ResourceId)
        .ToDictionary(group => group.Key, group => group.Sum(x => x.Cost), StringComparer.OrdinalIgnoreCase);

    string? topResourceId = null;
    decimal? topIncrease = null;
    foreach (var resourceId in latestByResource.Keys.Union(previousByResource.Keys, StringComparer.OrdinalIgnoreCase))
    {
        var latestCost = latestByResource.TryGetValue(resourceId, out var latestCostValue) ? latestCostValue : 0m;
        var previousCost = previousByResource.TryGetValue(resourceId, out var previousCostValue) ? previousCostValue : 0m;
        var delta = latestCost - previousCost;
        if (delta <= 0m)
        {
            continue;
        }

        if (topIncrease is null || delta > topIncrease.Value)
        {
            topIncrease = delta;
            topResourceId = resourceId;
        }
    }

    if (!string.IsNullOrWhiteSpace(topResourceId) && topIncrease is not null)
    {
        topCause = new DashboardCauseResourceResponse(
            ResourceId: topResourceId,
            ResourceName: ParseResourceName(topResourceId),
            ResourceType: ParseResourceType(topResourceId),
            IncreaseAmount: decimal.Round(topIncrease.Value, 4));
    }

    var confidence = await CalculateConfidenceAsync(db, userId.Value, latestDate.Value, cancellationToken);

    var summary = new DashboardSummaryResponse(
        Date: latestDate.Value,
        LatestDataDate: DateTime.SpecifyKind(latestDate.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
        YesterdayTotal: decimal.Round(previousTotal, 4),
        TodayTotal: decimal.Round(latestTotal, 4),
        Difference: decimal.Round(difference, 4),
        Baseline: decimal.Round(baseline, 4),
        MonthToDateTotal: decimal.Round(monthToDateTotal, 4),
        SpikeFlag: spikeFlag,
        Confidence: confidence,
        TopCauseResource: topCause,
        SuggestionText: spikeFlag
            ? "Spike detected. Review top cause resource."
            : "No spike detected today.");

    return Results.Ok(summary);
});

dashboard.MapGet("/history", async (
    ClaimsPrincipal principal,
    AppDbContext db,
    decimal? threshold,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var safeThreshold = threshold is null || threshold <= 0m ? 5m : threshold.Value;
    var currentDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);
    var toDate = await GetLatestCompleteBillingDateAsync(
        db,
        userId.Value,
        currentDate,
        cancellationToken);
    if (toDate is null)
    {
        return Results.Ok(new List<DashboardHistoryItemResponse>());
    }

    var fromDate = toDate.Value.AddDays(-6);
    var loadFromDate = fromDate.AddDays(-7);
    var rows = await db.DailyCostResources.AsNoTracking()
        .Where(x => x.UserId == userId.Value && x.Date >= loadFromDate && x.Date <= toDate.Value)
        .Select(x => new { x.Date, x.ResourceId, x.Cost })
        .ToListAsync(cancellationToken);

    var totalsByDate = rows
        .GroupBy(x => x.Date)
        .ToDictionary(group => group.Key, group => group.Sum(x => x.Cost));
    var resourceCostsByDate = rows
        .GroupBy(x => x.Date)
        .ToDictionary(
            dateGroup => dateGroup.Key,
            dateGroup => dateGroup
                .GroupBy(x => x.ResourceId)
                .ToDictionary(
                    resourceGroup => resourceGroup.Key,
                    resourceGroup => resourceGroup.Sum(x => x.Cost),
                    StringComparer.OrdinalIgnoreCase));

    var history = new List<DashboardHistoryItemResponse>();
    for (var offset = 0; offset < 7; offset++)
    {
        var date = toDate.Value.AddDays(-offset);
        var yesterdayDate = date.AddDays(-1);
        var totalToday = totalsByDate.TryGetValue(date, out var todayValue) ? todayValue : 0m;
        var totalYesterday = totalsByDate.TryGetValue(yesterdayDate, out var yesterdayValue) ? yesterdayValue : 0m;
        var difference = totalToday - totalYesterday;

        List<decimal> baselineValues = Enumerable.Range(1, 7)
            .Select(daysBack => date.AddDays(-daysBack))
            .Where(day => totalsByDate.ContainsKey(day))
            .Select(day => totalsByDate[day])
            .ToList();
        var baseline = baselineValues.Count > 0 ? baselineValues.Sum() / baselineValues.Count : 0m;
        var spikeFlag = baseline > 0m
            && totalToday > baseline * 1.5m
            && difference > safeThreshold;

        if (!spikeFlag && difference <= safeThreshold)
        {
            continue;
        }

        var todayByResource = resourceCostsByDate.TryGetValue(date, out var todayResources)
            ? todayResources
            : new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var yesterdayByResource = resourceCostsByDate.TryGetValue(yesterdayDate, out var yesterdayResources)
            ? yesterdayResources
            : new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        string? topResourceId = null;
        decimal? topIncreaseAmount = null;
        foreach (var resourceId in todayByResource.Keys.Union(yesterdayByResource.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var todayCost = todayByResource.TryGetValue(resourceId, out var todayCostValue) ? todayCostValue : 0m;
            var yesterdayCost = yesterdayByResource.TryGetValue(resourceId, out var yesterdayCostValue) ? yesterdayCostValue : 0m;
            var delta = todayCost - yesterdayCost;
            if (delta <= 0m)
            {
                continue;
            }

            if (topIncreaseAmount is null || delta > topIncreaseAmount.Value)
            {
                topIncreaseAmount = delta;
                topResourceId = resourceId;
            }
        }

        history.Add(new DashboardHistoryItemResponse(
            Date: date,
            YesterdayTotal: decimal.Round(totalYesterday, 4),
            TodayTotal: decimal.Round(totalToday, 4),
            Difference: decimal.Round(difference, 4),
            SpikeFlag: spikeFlag,
            TopResourceName: topResourceId is null ? null : ParseResourceName(topResourceId),
            TopIncreaseAmount: topIncreaseAmount is null ? null : decimal.Round(topIncreaseAmount.Value, 4)));
    }

    return Results.Ok(history);
});

dashboard.MapGet("/waste-findings", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var findings = await db.WasteFindings.AsNoTracking()
        .Where(x => x.UserId == userId.Value)
        .OrderByDescending(x => x.EstimatedMonthlyCost ?? 0m)
        .ThenByDescending(x => x.DetectedAtUtc)
        .Take(100)
        .Select(x => new DashboardWasteFindingResponse(
            x.FindingType,
            x.ResourceId,
            x.ResourceName,
            x.AzureSubscriptionId,
            x.EstimatedMonthlyCost,
            x.DetectedAtUtc,
            x.Status))
        .ToListAsync(cancellationToken);

    return Results.Ok(findings);
});

if (app.Environment.IsDevelopment())
{
    var dev = app.MapGroup("/dev").RequireAuthorization();

    dev.MapPost("/seed/cost-scenarios", async (
        SeedSyntheticCostDataRequest request,
        ClaimsPrincipal principal,
        IDevelopmentScenarioSeeder developmentScenarioSeeder,
        CancellationToken cancellationToken) =>
    {
        var userId = GetUserId(principal);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        try
        {
            var result = await developmentScenarioSeeder.SeedAsync(
                userId: userId.Value,
                scenario: string.IsNullOrWhiteSpace(request.Scenario) ? "normal" : request.Scenario,
                days: request.Days,
                clearExistingData: request.ClearExistingData,
                seed: request.Seed,
                cancellationToken: cancellationToken);

            return Results.Ok(new SeedSyntheticCostDataResponse(
                Scenario: result.Scenario,
                Days: result.Days,
                DailyCostRowsInserted: result.DailyCostRowsInserted,
                WasteFindingsInserted: result.WasteFindingsInserted,
                EventsGenerated: result.EventsGenerated,
                FromDate: result.FromDate,
                ToDate: result.ToDate,
                Note: result.Note));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    });
}

app.Run();

static Guid? GetUserId(ClaimsPrincipal principal)
{
    var userIdRaw = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
    return Guid.TryParse(userIdRaw, out var userId) ? userId : null;
}

static async Task ApplyMigrationsAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    try
    {
        await db.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Database migration was not applied. Start PostgreSQL and retry.");
    }
}

static bool IsAllowedFrontendOrigin(string? origin, IReadOnlyCollection<string> configuredAllowedOrigins)
{
    if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
    {
        return false;
    }

    var isLoopbackHost = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
    var isHttp = uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
        || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

    if (isLoopbackHost && isHttp)
    {
        return true;
    }

    return configuredAllowedOrigins.Any(allowed => string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase));
}

static async Task<DateOnly?> GetLatestCompleteBillingDateAsync(
    AppDbContext db,
    Guid userId,
    DateOnly currentDate,
    CancellationToken cancellationToken)
{
    var yesterday = currentDate.AddDays(-1);
    var dayBeforeYesterday = currentDate.AddDays(-2);
    var rows = await db.DailyCostResources.AsNoTracking()
        .Where(x => x.UserId == userId && x.Date <= yesterday)
        .Select(x => new { x.Date, x.ResourceId, x.Cost })
        .ToListAsync(cancellationToken);

    if (rows.Count == 0)
    {
        return null;
    }

    var dates = rows
        .Select(x => x.Date)
        .Distinct()
        .OrderByDescending(x => x)
        .ToList();

    // Prefer yesterday when present.
    if (dates.Contains(yesterday))
    {
        // Basic completeness guard: if yesterday is dramatically smaller than day-2,
        // treat it as incomplete and use day-2 instead.
        if (dates.Contains(dayBeforeYesterday))
        {
            var yesterdayRows = rows.Where(x => x.Date == yesterday).ToList();
            var dayBeforeRows = rows.Where(x => x.Date == dayBeforeYesterday).ToList();
            var yesterdayTotal = yesterdayRows.Sum(x => x.Cost);
            var dayBeforeTotal = dayBeforeRows.Sum(x => x.Cost);
            var yesterdayResources = yesterdayRows.Select(x => x.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var dayBeforeResources = dayBeforeRows.Select(x => x.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            var totalsSuggestIncomplete = dayBeforeTotal > 0m && yesterdayTotal < (dayBeforeTotal * 0.4m);
            var resourcesSuggestIncomplete = dayBeforeResources > 0 && yesterdayResources < Math.Max(1, (int)Math.Floor(dayBeforeResources * 0.4m));
            if (totalsSuggestIncomplete && resourcesSuggestIncomplete)
            {
                return dayBeforeYesterday;
            }
        }

        return yesterday;
    }

    // If yesterday is missing, use the newest date before yesterday.
    return dates[0];
}

static string ResolveCurrency(IEnumerable<string> currencies)
{
    var distinct = currencies
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => x.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    return distinct.Count switch
    {
        0 => "USD",
        1 => distinct[0],
        _ => "MIXED"
    };
}

static string ParseResourceName(string resourceId)
{
    if (string.IsNullOrWhiteSpace(resourceId))
    {
        return "Unknown Resource";
    }

    var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return parts.Length == 0 ? "Unknown Resource" : parts[^1];
}

static string ParseResourceType(string resourceId)
{
    if (string.IsNullOrWhiteSpace(resourceId))
    {
        return "unknown";
    }

    var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var providerIndex = Array.FindIndex(parts, part => part.Equals("providers", StringComparison.OrdinalIgnoreCase));
    if (providerIndex < 0 || providerIndex + 1 >= parts.Length)
    {
        return "unknown";
    }

    var provider = parts[providerIndex + 1];
    var typeSegments = new List<string>();
    for (var index = providerIndex + 2; index < parts.Length; index += 2)
    {
        typeSegments.Add(parts[index]);
    }

    return typeSegments.Count == 0 ? provider : $"{provider}/{string.Join("/", typeSegments)}";
}

static async Task<string> CalculateConfidenceAsync(
    AppDbContext db,
    Guid userId,
    DateOnly eventDate,
    CancellationToken cancellationToken)
{
    var yesterday = eventDate.AddDays(-1);
    var rows = await db.DailyCostResources.AsNoTracking()
        .Where(x => x.UserId == userId && (x.Date == eventDate || x.Date == yesterday))
        .Select(x => new { x.Date, x.ResourceId, x.Cost })
        .ToListAsync(cancellationToken);

    var todayByResource = rows
        .Where(x => x.Date == eventDate)
        .GroupBy(x => x.ResourceId)
        .ToDictionary(group => group.Key, group => group.Sum(x => x.Cost));

    var yesterdayByResource = rows
        .Where(x => x.Date == yesterday)
        .GroupBy(x => x.ResourceId)
        .ToDictionary(group => group.Key, group => group.Sum(x => x.Cost));

    var allResources = todayByResource.Keys
        .Union(yesterdayByResource.Keys)
        .Distinct(StringComparer.OrdinalIgnoreCase);

    var positiveDeltas = new List<decimal>();
    foreach (var resourceId in allResources)
    {
        var today = todayByResource.TryGetValue(resourceId, out var todayCost) ? todayCost : 0m;
        var yesterdayCost = yesterdayByResource.TryGetValue(resourceId, out var priorCost) ? priorCost : 0m;
        var delta = today - yesterdayCost;
        if (delta > 0m)
        {
            positiveDeltas.Add(delta);
        }
    }

    if (positiveDeltas.Count == 0)
    {
        return "Low";
    }

    positiveDeltas.Sort((left, right) => right.CompareTo(left));
    var top = positiveDeltas[0];
    var second = positiveDeltas.Count > 1 ? positiveDeltas[1] : 0m;
    var totalPositive = positiveDeltas.Sum();
    var topShare = totalPositive <= 0m ? 0m : top / totalPositive;
    var dominant = top >= 5m && (second <= 0m || top >= second * 2m || topShare >= 0.65m);
    if (dominant)
    {
        return "High";
    }

    return positiveDeltas.Count >= 2 ? "Medium" : "Low";
}
