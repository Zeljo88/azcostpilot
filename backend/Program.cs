using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AzCostPilot.Api.Contracts;
using AzCostPilot.Api.Services;
using AzCostPilot.Data;
using AzCostPilot.Data.Entities;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<ISecretEncryptionService, SecretEncryptionService>();
builder.Services.AddHttpClient<IAzureSubscriptionDiscoveryService, AzureSubscriptionDiscoveryService>();

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
        policy => policy.SetIsOriginAllowed(IsAllowedFrontendOrigin)
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

    return Results.Ok(new ConnectAzureResponse(
        Connected: true,
        ConnectionId: existing.Id,
        SubscriptionCount: discoveredSubscriptions.Count,
        Subscriptions: discoveredSubscriptions
            .Select(x => new ConnectedSubscriptionResponse(x.SubscriptionId, x.DisplayName, x.State))
            .ToList()));
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

    var latestEvent = await db.CostEvents.AsNoTracking()
        .Where(x => x.UserId == userId.Value)
        .OrderByDescending(x => x.Date)
        .ThenByDescending(x => x.CreatedAtUtc)
        .FirstOrDefaultAsync(cancellationToken);

    if (latestEvent is null)
    {
        var empty = new DashboardSummaryResponse(
            Date: DateOnly.FromDateTime(DateTime.UtcNow.Date),
            YesterdayTotal: 0m,
            TodayTotal: 0m,
            Difference: 0m,
            Baseline: 0m,
            SpikeFlag: false,
            TopCauseResource: null,
            SuggestionText: "No cost event yet. Run worker ingestion to generate a daily summary.");
        return Results.Ok(empty);
    }

    DashboardCauseResourceResponse? topCause = null;
    if (!string.IsNullOrWhiteSpace(latestEvent.TopResourceId) && latestEvent.TopIncreaseAmount is not null)
    {
        topCause = new DashboardCauseResourceResponse(
            ResourceId: latestEvent.TopResourceId,
            ResourceName: latestEvent.TopResourceName ?? ParseResourceName(latestEvent.TopResourceId),
            ResourceType: latestEvent.TopResourceType ?? ParseResourceType(latestEvent.TopResourceId),
            IncreaseAmount: latestEvent.TopIncreaseAmount.Value);
    }

    var summary = new DashboardSummaryResponse(
        Date: latestEvent.Date,
        YesterdayTotal: latestEvent.TotalYesterday,
        TodayTotal: latestEvent.TotalToday,
        Difference: latestEvent.Difference,
        Baseline: latestEvent.Baseline,
        SpikeFlag: latestEvent.SpikeFlag,
        TopCauseResource: topCause,
        SuggestionText: string.IsNullOrWhiteSpace(latestEvent.SuggestionText)
            ? (latestEvent.SpikeFlag ? "Spike detected. Review top cause resource." : "No spike detected today.")
            : latestEvent.SuggestionText);

    return Results.Ok(summary);
});

dashboard.MapGet("/history", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken) =>
{
    var userId = GetUserId(principal);
    if (userId is null)
    {
        return Results.Unauthorized();
    }

    var history = await db.CostEvents.AsNoTracking()
        .Where(x => x.UserId == userId.Value)
        .OrderByDescending(x => x.Date)
        .ThenByDescending(x => x.CreatedAtUtc)
        .Take(10)
        .Select(x => new DashboardHistoryItemResponse(
            x.Date,
            x.TotalYesterday,
            x.TotalToday,
            x.Difference,
            x.SpikeFlag,
            x.TopResourceId,
            x.TopResourceName,
            x.TopIncreaseAmount,
            string.IsNullOrWhiteSpace(x.SuggestionText)
                ? (x.SpikeFlag ? "Spike detected." : "No spike detected.")
                : x.SuggestionText!))
        .ToListAsync(cancellationToken);

    return Results.Ok(history);
});

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

static bool IsAllowedFrontendOrigin(string? origin)
{
    if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
    {
        return false;
    }

    var isLoopbackHost = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
    var isHttp = uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
        || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

    return isLoopbackHost && isHttp;
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
