using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using XGArcade.Api.Auth;
using XGArcade.Api.Grid;
using XGArcade.Api.Guesses;
using XGArcade.Api.Rounds;
using XGArcade.Core.Auth;
using XGArcade.Core.Games;
using XGArcade.Core.Rounds;
using XGArcade.Core.Scoring;
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
    // S-009: backfills Player.NormalizedFullName for any row that predates
    // that column (or predates PlayerNameNormalizer's punctuation-stripping
    // fix) — see PlayerNormalizedFullNameBackfiller's own doc comment.
    await PlayerNormalizedFullNameBackfiller.BackfillAsync(migrationDbContext);

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

// COMP-05 (Games.XGGrid) — S-007's grid generation.
builder.Services.AddSingleton(new GridGenerationOptions());
builder.Services.AddScoped<IGridInstanceRepository, GridInstanceRepository>();
builder.Services.AddScoped<IGameModule, GridGameModule>();
builder.Services.AddScoped<IGameModuleResolver, GameModuleResolver>();

// COMP-03 (Core.Rounds) — S-008's round generation/scheduling (REQ-301) and
// round-close (REQ-205's stub half; S-011 extends RoundCloseService with
// real scoring once Guess/Core.Scoring exist).
builder.Services.AddSingleton(TimeProvider.System);
// RoundDuration must be at least as long as the longest gap between two
// consecutive generate-round.yml cron firings (Tue+Fri weekly: Fri->Tue is
// 4 days, the longer of its two alternating gaps), or a round can close
// before the next scheduled run generates its successor — see
// RoundSchedulingOptions' own doc comment and NOTES.md for the full
// derivation. Change this together with generate-round.yml's cron, never
// independently.
builder.Services.AddSingleton(new RoundSchedulingOptions
{
    GameKey = GridGameModule.XGGridGameKey,
    RoundDuration = TimeSpan.FromDays(4),
});
builder.Services.AddScoped<IRoundRepository, RoundRepository>();
builder.Services.AddScoped<IRoundGenerationService, RoundGenerationService>();
builder.Services.AddScoped<IRoundCloseService, RoundCloseService>();

// COMP-04 (Core.Scoring) — S-009's guess submission (REQ-201/202/203/208/210).
builder.Services.AddScoped<IGuessRepository, GuessRepository>();
builder.Services.AddScoped<IGuessSubmissionService, GuessSubmissionService>();

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

            // ADR-0017: Supabase signs production tokens with its rotating
            // asymmetric JWT Signing Keys system (a `kid` header claim
            // identifies which key), verified via a JWKS endpoint — not a
            // static shared secret, which is what this branch assumed until
            // a real deployment surfaced IDX10503 "Number of keys in
            // Configuration: '0'" (NOTES.md, 2026-07-10). The path is
            // configurable (not a bare literal) so it can be corrected via
            // an env var alone, no rebuild, if live testing shows it wrong.
            var jwksPath = builder.Configuration["Auth:SupabaseJwksPath"] ?? "/auth/v1/.well-known/jwks.json";
            var jwksAddress = supabaseUrl.TrimEnd('/') + jwksPath;

            options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                jwksAddress,
                new SupabaseJwksConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = jwksAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase) });

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                ValidateIssuer = true,
                ValidIssuer = $"{supabaseUrl.TrimEnd('/')}/auth/v1",
                ValidateAudience = true,
                ValidAudience = "authenticated",
                ValidateLifetime = true,
            };

            // The one time this matters is exactly when the JWKS fetch/parse
            // itself is broken (e.g. wrong path) — the default JwtBearer
            // failure log gives no indication why. See ADR-0017's
            // rollout-risk note: this is the log line that turns the next
            // failed login into an actionable message instead of another
            // bare signature-mismatch dead end.
            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    var logger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("XGArcade.Api.Auth.SupabaseJwt");
                    logger.LogError(context.Exception,
                        "JWT validation failed (JWKS endpoint: {JwksAddress}).", jwksAddress);
                    return Task.CompletedTask;
                },
            };
        }
    });

builder.Services.AddAuthorization();

builder.Services.AddControllers();

var app = builder.Build();

// ADR-0017's rollout-risk mitigation: fires unconditionally at boot, before
// anyone can even attempt to log in, so the very first thing visible in the
// log stream after a deploy is the resolved JWKS address — if the path is
// wrong, that's visible within seconds of checking, not after a confused
// user reports a login failure.
if (!useLocalE2EAuth)
{
    // Safe: the AddJwtBearer setup above already did `?? throw` on this
    // exact key for this same (!useLocalE2EAuth) branch — if we reached
    // here, it was present.
    var configuredSupabaseUrl = app.Configuration["Supabase:Url"]!;
    var configuredJwksPath = app.Configuration["Auth:SupabaseJwksPath"] ?? "/auth/v1/.well-known/jwks.json";
    app.Logger.LogInformation(
        "JWT validation configured against Supabase JWKS endpoint {JwksAddress}.",
        configuredSupabaseUrl.TrimEnd('/') + configuredJwksPath);
}

// Configure the HTTP request pipeline.

app.UseHttpsRedirection();

app.UseCors("Frontend");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapInternalGridEndpoints();
app.MapInternalRoundEndpoints();
app.MapRoundEndpoints();
app.MapGuessEndpoints();

app.Run();

// Marker partial (global namespace, matching the compiler-generated Program
// class from the top-level statements above) so WebApplicationFactory<Program>
// in XGArcade.Api.Tests can reference it across the assembly boundary.
public partial class Program;
