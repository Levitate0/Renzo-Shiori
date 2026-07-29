using RenzoBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RenzoBackend.Migrations;

/// <summary>
/// Per-series provider ordering rank (0 = highest priority, lower wins). Drives read/preview source
/// selection with step-down fallback, the download source, and merged-chapter attribution, giving the
/// user chapter-quality control per series. Defaults to 0; a one-time startup backfill assigns a
/// sensible per-series ranking (storage-first) so the previous behavior is preserved.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260728000000_AddProviderPriority")]
public partial class AddProviderPriority : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Priority",
            table: "SeriesProviders",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Priority",
            table: "SeriesProviders");
    }
}
