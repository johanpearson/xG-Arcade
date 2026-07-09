using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using XGArcade.Api.Auth;
using XGArcade.Api.Grid;
using XGArcade.Core.Auth;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Repositories;
using XGArcade.Data.Seeding;
using XGArcade.DataSync.Wikidata;
using XGArcade.Games.XGGrid;

// `dotnet run -- migrate-and-seed` is a distinct CLI verb (not a normal
// server start) used by ci.yml's local E2E stack. Applies pending EF Core
// migrations against ConnectionStrings:Database, then seeds Tier 0's
// hand-curated reference data (S-005) — idempotent, safe to re-run.
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
    await ReferenceDataSeeder.SeedAsync(migrationDbContext);

    Console.WriteLine("migrate-and-seed: migrations applied, reference data seeded.");
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

var databaseConnectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is not configured.");

builder.Services.AddDbContext<XGArcadeDbContext>(options =>
    options.UseNpgsql(databaseConnectionString));

// COMP-06 (Data.PlayerStore) — the only path to category/player data;
// see architecture-document.md boundary rule 1.
builder.Services.AddScoped<ICategoryValueRepository, CategoryValueRepository>();
builder.Services.AddScoped<IPlayerStoreRepository, PlayerStoreRepository>();

// COMP-01 (Core.Users) — the only path to the local User profile table.
builder.Services.AddScoped<IUserRepository, UserRepository>();

// COMP-07 (DataSync.Clients), Tier 0 half: SPARQL against Wikidata Query
// Service, per implementation-document.md §6a. No API-Football fallback
// client yet — that's Tier 1 (ADR-0011). Not yet called from any endpoint;
// S-007 (grid generation) is the first caller.
builder.Services.AddHttpClient<IWikidataClient, WikidataClient>(client =>
{
    client.BaseAddress = new Uri("https://query.wikidata.org/");
    // WDQS's own etiquette guidance asks for an identifiable User-Agent
    // rather than a generic HttpClient default.
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "xG-Arcade/1.0 (Tier 0 grid data sync; see docs/decisions/0011-wikidata-first-lookup-waterfall.md)");
});
builder.Services.AddScoped<IWikidataLookupService, WikidataLookupService>();

// COMP-05 (Games.XGGrid) — S-007's grid generation. Only one game module
// exists yet, so a direct IGameModule registration is enough; resolving
// several modules by GameKey is S-008's job (Round Scheduler) once a second
// game or real round scheduling exists.
builder.Services.AddSingleton(new GridGenerationOptions());
builder.Services.AddScoped<IGridInstanceRepository, GridInstanceRepository>();
builder.Services.AddScoped<IGameModule, GridGameModule>();

// ci.yml's local E2E stack has no live Supabase project to call, so it sets
// Auth:Mode=local-e2e to swap in a fake ISupabaseAuthClient + a locally
// signed JWT instead. Re-check the environment here rather than trusting
// the config flag alone — same "never guarded only by config/an attribute"
// principle CLAUDE.md establishes for COMP-09's Testing.SeedManager
// (ADR-0006) — so this can never accidentally activate outside Development.
var useLocalE2EAuth = builder.Configuration["Auth:Mode"] == "local-e2e" && builder.Environment.IsDevelopment();

if (useLocalE2EAuth)
{
    builder.Services.AddSingleton<ISupabaseAuthClient, LocalE2EAuthClient>();
}
else
{
    // Signup/login are mediated through Supabase Auth's REST API rather
    // than the frontend calling Supabase directly — see ADR-0013.
    var supabaseUrl = builder.Configuration["Supabase:Url"]
        ?? throw new InvalidOperationException("Supabase:Url is not configured.");
    var supabaseAnonKey = builder.Configuration["Supabase:AnonKey"]
        ?? throw new InvalidOperationException("Supabase:AnonKey is not configured.");

    builder.Services.AddHttpClient<ISupabaseAuthClient, SupabaseAuthClient>(client =>
    {
        client.BaseAddress = new Uri(supabaseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("apikey", supabaseAnonKey);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {supabaseAnonKey}");
    });
}

// JWT validation middleware (REQ-606's pipeline): backend never manages
// passwords, only validates the tokens Supabase Auth (or, in local-e2e
// mode, LocalE2EAuthClient) already issued.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep claim types as issued ("sub", "role", ...) instead of ASP.NET
        // Core's legacy remap to long XML-Soap URIs.
        options.MapInboundClaims = false;

        if (useLocalE2EAuth)
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = LocalE2EAuth.SigningKey,
                ValidateIssuer = true,
                ValidIssuer = LocalE2EAuth.Issuer,
                ValidateAudience = true,
                ValidAudience = LocalE2EAuth.Audience,
                ValidateLifetime = true,
            };
        }
        else
        {
            var supabaseUrl = builder.Configuration["Supabase:Url"]
                ?? throw new InvalidOperationException("Supabase:Url is not configured.");
            var supabaseJwtSecret = builder.Configuration["Auth:SupabaseJwtSecret"]
                ?? throw new InvalidOperationException("Auth:SupabaseJwtSecret is not configured.");

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(supabaseJwtSecret)),
                ValidateIssuer = true,
                ValidIssuer = $"{supabaseUrl.TrimEnd('/')}/auth/v1",
                ValidateAudience = true,
                ValidAudience = "authenticated",
                ValidateLifetime = true,
            };
        }
    });

builder.Services.AddAuthorization();

builder.Services.AddControllers();

var app = builder.Build();

// Configure the HTTP request pipeline.

app.UseHttpsRedirection();

app.UseCors("Frontend");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapInternalGridEndpoints();

app.Run();

// Marker partial (global namespace, matching the compiler-generated Program
// class from the top-level statements above) so WebApplicationFactory<Program>
// in XGArcade.Api.Tests can reference it across the assembly boundary.
public partial class Program;
