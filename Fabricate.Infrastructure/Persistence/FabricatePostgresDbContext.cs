using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fabricate.Infrastructure.Persistence;

/// <summary>
/// The PostgreSQL flavour of <see cref="FabricateDbContext"/>. EF Core migrations are provider-specific, and EF
/// selects migrations by the exact context type in their <c>[DbContext]</c> attribute, so this subclass exists purely
/// to own the PostgreSQL migration set under <c>Persistence/Migrations/Postgres</c>. The model is identical.
/// Repositories keep depending on <see cref="FabricateDbContext"/>; DI resolves it to this type when
/// <c>FABRICATE_DB_PROVIDER=postgres</c>.
/// </summary>
public sealed class FabricatePostgresDbContext(DbContextOptions<FabricatePostgresDbContext> options) : FabricateDbContext(options);

/// <summary>Design-time factory so <c>dotnet ef migrations add … --context FabricatePostgresDbContext</c> works without a live database.</summary>
public sealed class FabricatePostgresDbContextFactory : IDesignTimeDbContextFactory<FabricatePostgresDbContext>
{
    public FabricatePostgresDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<FabricatePostgresDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=fabricate_design_time;Username=postgres;Password=postgres");
        return new FabricatePostgresDbContext(optionsBuilder.Options);
    }
}
