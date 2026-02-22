using AzCostPilot.Worker;
using AzCostPilot.Worker.Services;
using AzCostPilot.Data;
using AzCostPilot.Data.Services;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddSingleton<ISecretEncryptionService, SecretEncryptionService>();
builder.Services.AddSingleton<ISecretCipher>(sp => sp.GetRequiredService<ISecretEncryptionService>());
builder.Services.AddHttpClient<ICostSyncService, CostSyncService>();
builder.Services.AddHttpClient<IWasteFindingService, WasteFindingService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
