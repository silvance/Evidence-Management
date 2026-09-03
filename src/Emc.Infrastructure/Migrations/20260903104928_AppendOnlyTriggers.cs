using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Emc.Infrastructure.Migrations;

/// <summary>
/// Layer 3 of the append-only enforcement (docs/architecture.md §4.2).
///
/// Installs INSTEAD OF UPDATE / DELETE triggers on the accountability tables. Layers 1 and 2
/// (domain immutability and the SaveChanges guard) protect against mistakes made THROUGH the
/// application; this layer protects against changes made OUTSIDE it, including by an
/// administrator using SSMS - which is the case IAM-009 is actually about.
///
/// SQL Server only. SQLite test runs exercise layers 1 and 2, which have their own tests.
/// </summary>
public partial class AppendOnlyTriggers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        if (!IsSqlServer(migrationBuilder))
        {
            return;
        }

        foreach (var sql in global::Emc.Infrastructure.Persistence.AppendOnlyTriggers.All)
        {
            migrationBuilder.Sql(sql);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        if (!IsSqlServer(migrationBuilder))
        {
            return;
        }

        foreach (var sql in global::Emc.Infrastructure.Persistence.AppendOnlyTriggers.DropAll)
        {
            migrationBuilder.Sql(sql);
        }
    }

    private static bool IsSqlServer(MigrationBuilder migrationBuilder)
        => migrationBuilder.ActiveProvider?.Contains("SqlServer", StringComparison.Ordinal) == true;
}
