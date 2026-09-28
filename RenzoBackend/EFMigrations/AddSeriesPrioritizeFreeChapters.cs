using RenzoBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RenzoBackend.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260927220000_AddSeriesPrioritizeFreeChapters")]
public partial class AddSeriesPrioritizeFreeChapters : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Default false: existing libraries keep behaving exactly as they do now
        // until the toggle is turned on for a series.
        migrationBuilder.AddColumn<bool>(
            name: "PrioritizeFreeChapters",
            table: "Series",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PrioritizeFreeChapters",
            table: "Series");
    }
}
