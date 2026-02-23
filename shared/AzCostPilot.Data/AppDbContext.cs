using AzCostPilot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AzCostPilot.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<AzureConnection> AzureConnections => Set<AzureConnection>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<DailyCostResource> DailyCostResources => Set<DailyCostResource>();

    public DbSet<CostEvent> CostEvents => Set<CostEvent>();

    public DbSet<WasteFinding> WasteFindings => Set<WasteFinding>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Email).HasMaxLength(320).IsRequired();
            entity.Property(x => x.PasswordHash).IsRequired();
            entity.Property(x => x.CreatedAtUtc).IsRequired();
            entity.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<AzureConnection>(entity =>
        {
            entity.ToTable("azure_connections");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ClientId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.EncryptedClientSecret).IsRequired();
            entity.Property(x => x.CreatedAtUtc).IsRequired();

            entity.HasOne(x => x.User)
                .WithMany(x => x.AzureConnections)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => new { x.UserId, x.TenantId, x.ClientId }).IsUnique();
        });

        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.ToTable("subscriptions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AzureSubscriptionId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.CreatedAtUtc).IsRequired();

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.AzureConnection)
                .WithMany(x => x.Subscriptions)
                .HasForeignKey(x => x.AzureConnectionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => new { x.UserId, x.AzureSubscriptionId }).IsUnique();
        });

        modelBuilder.Entity<DailyCostResource>(entity =>
        {
            entity.ToTable("daily_cost_resource");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AzureSubscriptionId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ResourceId).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.Cost).HasColumnType("numeric(18,4)");
            entity.Property(x => x.Currency).HasMaxLength(16).IsRequired();
            entity.HasIndex(x => new { x.UserId, x.AzureSubscriptionId, x.Date, x.ResourceId }).IsUnique();
        });

        modelBuilder.Entity<CostEvent>(entity =>
        {
            entity.ToTable("cost_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TotalYesterday).HasColumnType("numeric(18,4)");
            entity.Property(x => x.TotalToday).HasColumnType("numeric(18,4)");
            entity.Property(x => x.Difference).HasColumnType("numeric(18,4)");
            entity.Property(x => x.Baseline).HasColumnType("numeric(18,4)");
            entity.Property(x => x.TopIncreaseAmount).HasColumnType("numeric(18,4)");
            entity.Property(x => x.TopResourceId).HasMaxLength(1024);
            entity.Property(x => x.TopResourceName).HasMaxLength(256);
            entity.Property(x => x.TopResourceType).HasMaxLength(256);
            entity.Property(x => x.SuggestionText).HasMaxLength(1024);
            entity.Property(x => x.CreatedAtUtc).IsRequired();
            entity.HasIndex(x => new { x.UserId, x.Date });
        });

        modelBuilder.Entity<WasteFinding>(entity =>
        {
            entity.ToTable("waste_findings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AzureSubscriptionId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.FindingType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ResourceId).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.ResourceName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.EstimatedMonthlyCost).HasColumnType("numeric(18,4)");
            entity.Property(x => x.Classification).HasMaxLength(64);
            entity.Property(x => x.InactiveDurationDays).HasColumnType("numeric(8,2)");
            entity.Property(x => x.WasteConfidenceLevel).HasMaxLength(16);
            entity.Property(x => x.LastSeenActiveUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.Status).HasMaxLength(64).IsRequired();
            entity.Property(x => x.DetectedAtUtc).IsRequired();
            entity.HasIndex(x => new { x.UserId, x.FindingType, x.ResourceId });
        });
    }
}
