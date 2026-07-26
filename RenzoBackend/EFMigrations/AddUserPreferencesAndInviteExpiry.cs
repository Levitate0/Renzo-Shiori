using RenzoBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RenzoBackend.Migrations;

/// <summary>
/// Adds per-user UI preferences (theme/accent, persisted server-side so
/// appearance follows the account) and an expiry for the password-set (invite)
/// token so invites can be hardened to single-use + expiring like reset tokens.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260726000000_AddUserPreferencesAndInviteExpiry")]
public partial class AddUserPreferencesAndInviteExpiry : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Preferences",
            table: "Users",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "PasswordSetTokenExpiresAt",
            table: "Users",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Preferences", table: "Users");
        migrationBuilder.DropColumn(name: "PasswordSetTokenExpiresAt", table: "Users");
    }
}
