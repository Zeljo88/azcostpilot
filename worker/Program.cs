using AzCostPilot.Worker;
using AzCostPilot.Worker.Services;
using AzCostPilot.Data;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddSingleton<ISecretEncryptionService, SecretEncryptionService>();
builder.Services.AddHttpClient<ICostIngestionService, CostIngestionService>();
builder.Services.AddSingleton<ICostEventDetectionService, CostEventDetectionService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
