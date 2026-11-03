using RenzoBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RenzoBackend.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261103220000_AddSeriesAutoBuyPaidChapters")]
public partial class AddSeriesAutoBuyPaidChapters : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Null: every series follows the account setting, which starts off.
        migrationBuilder.AddColumn<bool>(
            name: "AutoBuyPaidChapters",
            table: "Series",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AutoBuyPaidChapters",
            table: "Series");
    }
}
