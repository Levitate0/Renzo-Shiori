using RensaioBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RensaioBackend.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260709120000_AddImportEntityIsTitleOnly")]
public partial class AddImportEntityIsTitleOnly : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsTitleOnly",
            table: "Imports",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "IsTitleOnly",
            table: "Imports");
    }
}
