using RenzoBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RenzoBackend.Migrations;

/// <summary>
/// Per-user content preferences: preferred languages, 18+ visibility and
/// download-all-chapters move from the single global settings blob onto the
/// user.
///
/// All three are NULLABLE and default to NULL, meaning "inherit the server
/// default". That is deliberate: on upgrade every existing account keeps
/// behaving exactly as the global setting says, and nothing has to be
/// backfilled. It also leaves a sane answer for the background jobs — library
/// scans, download sweeps — which run with no user at all.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260907190000_AddUserContentPreferences")]
public partial class AddUserContentPreferences : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PreferredLanguages",
            table: "Users",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "NsfwVisibility",
            table: "Users",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "DownloadAllChapters",
            table: "Users",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PreferredLanguages", table: "Users");
        migrationBuilder.DropColumn(name: "NsfwVisibility", table: "Users");
        migrationBuilder.DropColumn(name: "DownloadAllChapters", table: "Users");
    }
}
