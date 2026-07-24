using RenzoBackend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RenzoBackend.Migrations;

/// <summary>
/// Per-user saved values for extension preferences on a shared source (e.g.
/// MangaDex content rating). Save-and-apply model: not simultaneous per-user
/// isolation — see UserProviderPreferenceEntity doc comment.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260720233000_AddUserProviderPreferences")]
public partial class AddUserProviderPreferences : Microsoft.EntityFrameworkCore.Migrations.Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UserProviderPreferences",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                PkgName = table.Column<string>(type: "TEXT", nullable: false, collation: "BINARY"),
                PreferenceIndex = table.Column<int>(type: "INTEGER", nullable: false),
                ValueJson = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserProviderPreferences", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UserProviderPreference_User_Pkg_Index",
            table: "UserProviderPreferences",
            columns: new[] { "UserId", "PkgName", "PreferenceIndex" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "UserProviderPreferences");
    }
}
