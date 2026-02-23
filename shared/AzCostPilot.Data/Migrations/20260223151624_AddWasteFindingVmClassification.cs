using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzCostPilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWasteFindingVmClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Classification",
                table: "waste_findings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InactiveDurationDays",
                table: "waste_findings",
                type: "numeric(8,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenActiveUtc",
                table: "waste_findings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WasteConfidenceLevel",
                table: "waste_findings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Classification",
                table: "waste_findings");

            migrationBuilder.DropColumn(
                name: "InactiveDurationDays",
                table: "waste_findings");

            migrationBuilder.DropColumn(
                name: "LastSeenActiveUtc",
                table: "waste_findings");

            migrationBuilder.DropColumn(
                name: "WasteConfidenceLevel",
                table: "waste_findings");
        }
    }
}
