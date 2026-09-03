using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Emc.Infrastructure.Persistence;

/// <summary>
/// Design-time factory, used only by `dotnet ef` to create migrations.
///
/// The connection string here is never used at runtime and never connects during migration
/// generation. AUD-012: migrations are source-controlled and applied by a deliberate deployment
/// step with a higher-privilege login - the application NEVER migrates on startup, because
/// silent schema change on an accountability system is unacceptable.
/// </summary>
public sealed class EmcDbContextFactory : IDesignTimeDbContextFactory<EmcDbContext>
{
    public EmcDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<EmcDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=EmcDesignTime;Trusted_Connection=True;")
            .Options;

        return new EmcDbContext(options);
    }
}
