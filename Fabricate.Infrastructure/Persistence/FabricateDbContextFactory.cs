using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fabricate.Infrastructure.Persistence;

public sealed class FabricateDbContextFactory : IDesignTimeDbContextFactory<FabricateDbContext>
{
    public FabricateDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<FabricateDbContext>();
        optionsBuilder.UseSqlite("Data Source=design-time.db");
        return new FabricateDbContext(optionsBuilder.Options);
    }
}
