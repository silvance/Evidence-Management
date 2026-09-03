using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Emc.Infrastructure.Migrations;

/// <summary>
/// Layer 3 of the append-only enforcement (docs/architecture.md §4.2).
///
/// INSERT ONLY: every trigger rejects unconditionally. There is no permitted UPDATE to carve out,
/// because corrections use backward references and never touch the corrected row.
///
/// SQL Server only. SQLite test runs exercise layers 1 and 2; the trigger layer is covered by the
/// SQL Server integration suite.
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
