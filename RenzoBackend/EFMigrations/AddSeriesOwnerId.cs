using RenzoBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RenzoBackend.Migrations;

/// <summary>
/// Per-user library separation: each series now has an owning user. Existing
/// rows default to Guid.Empty and are backfilled to the admin/owner account by
/// StartupHostedService on first boot after this migration.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260720180000_AddSeriesOwnerId")]
public partial class AddSeriesOwnerId : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "OwnerId",
            table: "Series",
            type: "TEXT",
            nullable: false,
            defaultValue: Guid.Empty);

        migrationBuilder.CreateIndex(
            name: "IX_Series_OwnerId",
            table: "Series",
            column: "OwnerId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Series_OwnerId",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "OwnerId",
            table: "Series");
    }
}
