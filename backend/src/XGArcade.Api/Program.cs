using Microsoft.EntityFrameworkCore;
using XGArcade.Data;
using XGArcade.Data.Repositories;

// `dotnet run -- migrate-and-seed` is a distinct CLI verb (not a normal
// server start) used by ci.yml's local E2E stack. Applies pending EF Core
// migrations against ConnectionStrings:Database; seeding reference data is
// S-005's job (still a no-op here until that story lands).
if (args is ["migrate-and-seed"])
{
    var migrationConfig = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build();

    var connectionString = migrationConfig.GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

    var optionsBuilder = new DbContextOptionsBuilder<XGArcadeDbContext>()
        .UseNpgsql(connectionString);

    await using var migrationDbContext = new XGArcadeDbContext(optionsBuilder.Options);
    await migrationDbContext.Database.MigrateAsync();

    Console.WriteLine("migrate-and-seed: migrations applied. Seeding lands in S-005 — nothing to seed yet.");
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

var corsAllowedOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options =>
{
    // REQ-606: restricted to known frontend origin(s), never a wildcard.
    // No configured origin (e.g. before DEV_FRONTEND_HOSTNAME is filled in
    // post-first-deploy) means the policy allows nothing rather than falling
    // back to permissive.
    options.AddPolicy("Frontend", policy => policy.WithOrigins(corsAllowedOrigins).AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddDbContext<XGArcadeDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));

// COMP-06 (Data.PlayerStore) — the only path to category/player data;
// see architecture-document.md boundary rule 1.
builder.Services.AddScoped<ICategoryValueRepository, CategoryValueRepository>();
builder.Services.AddScoped<IPlayerStoreRepository, PlayerStoreRepository>();

builder.Services.AddControllers();

var app = builder.Build();

// Configure the HTTP request pipeline.

app.UseHttpsRedirection();

app.UseCors("Frontend");

app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

// Marker partial (global namespace, matching the compiler-generated Program
// class from the top-level statements above) so WebApplicationFactory<Program>
// in XGArcade.Api.Tests can reference it across the assembly boundary.
public partial class Program;
