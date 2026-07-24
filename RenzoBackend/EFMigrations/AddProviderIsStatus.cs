using RenzoBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RenzoBackend.Migrations;

/// <summary>
/// Per-source "this provider is the authority for the series status" flag,
/// mirroring IsTitle/IsCover. Defaults to false; the permanent (storage) source
/// is seeded as the status source at startup for existing series.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260720120000_AddProviderIsStatus")]
public partial class AddProviderIsStatus : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsStatus",
            table: "SeriesProviders",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "IsStatus",
            table: "SeriesProviders");
    }
}
