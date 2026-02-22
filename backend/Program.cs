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
        policy => policy.WithOrigins("http://localhost:4200")
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
    var userIdRaw = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
    var email = principal.FindFirstValue(JwtRegisteredClaimNames.Email);
    if (string.IsNullOrWhiteSpace(userIdRaw) || !Guid.TryParse(userIdRaw, out var userId))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new { userId, email });
}).RequireAuthorization();

var connections = app.MapGroup("/connections").RequireAuthorization();

connections.MapPost("/azure", async (SaveAzureConnectionRequest request, ClaimsPrincipal principal, AppDbContext db, ISecretEncryptionService secretEncryptionService, CancellationToken cancellationToken) =>
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

    var existing = await db.AzureConnections.FirstOrDefaultAsync(
        x => x.UserId == userId.Value && x.TenantId == request.TenantId && x.ClientId == request.ClientId,
        cancellationToken);

    if (existing is null)
    {
        existing = new AzureConnection
        {
            Id = Guid.NewGuid(),
            UserId = userId.Value,
            TenantId = request.TenantId.Trim(),
            ClientId = request.ClientId.Trim(),
            EncryptedClientSecret = secretEncryptionService.Encrypt(request.ClientSecret),
            CreatedAtUtc = DateTime.UtcNow
        };
        db.AzureConnections.Add(existing);
    }
    else
    {
        existing.EncryptedClientSecret = secretEncryptionService.Encrypt(request.ClientSecret);
    }

    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new
    {
        connectionId = existing.Id,
        existing.TenantId,
        existing.ClientId
    });
});

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

app.Run();

static Guid? GetUserId(ClaimsPrincipal principal)
{
    var userIdRaw = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
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
