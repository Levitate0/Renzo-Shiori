using RensaioBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RensaioBackend.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260709120000_AddSeriesDateAdded")]
public partial class AddSeriesDateAdded : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "DateAdded",
            table: "Series",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DateAdded",
            table: "Series");
    }
}
