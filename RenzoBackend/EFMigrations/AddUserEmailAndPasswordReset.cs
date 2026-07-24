using RenzoBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RenzoBackend.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260710120000_AddUserEmailAndPasswordReset")]
public partial class AddUserEmailAndPasswordReset : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Email",
            table: "Users",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PasswordResetTokenHash",
            table: "Users",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "PasswordResetExpiresAt",
            table: "Users",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Email",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "PasswordResetTokenHash",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "PasswordResetExpiresAt",
            table: "Users");
    }
}
