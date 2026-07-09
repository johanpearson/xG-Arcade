using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace XGArcade.Data;

// Design-time factory so `dotnet ef migrations add/database update` can
// construct the context without booting the full API host. Only used by EF
// Core tooling — the running app builds its DbContextOptions from
// XGArcade.Api's own configuration (ConnectionStrings:Database).
public class XGArcadeDbContextFactory : IDesignTimeDbContextFactory<XGArcadeDbContext>
{
    public XGArcadeDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Database")
            ?? "Host=localhost;Database=xgarcade_e2e;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseNpgsql(connectionString);

        return new XGArcadeDbContext(optionsBuilder.Options);
    }
}
