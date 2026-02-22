using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AzCostPilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cost_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    TotalYesterday = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    TotalToday = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Difference = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Baseline = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    SpikeFlag = table.Column<bool>(type: "boolean", nullable: false),
                    TopResourceId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    TopResourceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TopResourceType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TopIncreaseAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    SuggestionText = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cost_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "daily_cost_resource",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AzureSubscriptionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Cost = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_cost_resource", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "waste_findings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AzureSubscriptionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FindingType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ResourceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EstimatedMonthlyCost = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    Status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DetectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_waste_findings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "azure_connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EncryptedClientSecret = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_azure_connections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_azure_connections_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AzureConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AzureSubscriptionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_subscriptions_azure_connections_AzureConnectionId",
                        column: x => x.AzureConnectionId,
                        principalTable: "azure_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_subscriptions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_azure_connections_UserId_TenantId_ClientId",
                table: "azure_connections",
                columns: new[] { "UserId", "TenantId", "ClientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cost_events_UserId_Date",
                table: "cost_events",
                columns: new[] { "UserId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_daily_cost_resource_UserId_AzureSubscriptionId_Date_Resourc~",
                table: "daily_cost_resource",
                columns: new[] { "UserId", "AzureSubscriptionId", "Date", "ResourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_AzureConnectionId",
                table: "subscriptions",
                column: "AzureConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_UserId_AzureSubscriptionId",
                table: "subscriptions",
                columns: new[] { "UserId", "AzureSubscriptionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_waste_findings_UserId_FindingType_ResourceId",
                table: "waste_findings",
                columns: new[] { "UserId", "FindingType", "ResourceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cost_events");

            migrationBuilder.DropTable(
                name: "daily_cost_resource");

            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropTable(
                name: "waste_findings");

            migrationBuilder.DropTable(
                name: "azure_connections");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
