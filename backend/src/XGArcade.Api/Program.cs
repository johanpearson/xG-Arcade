// `dotnet run -- migrate-and-seed` is a distinct CLI verb (not a normal
// server start) used by ci.yml's local E2E stack. Today it's a no-op stub —
// there's no EF Core context or migration to run until S-003 — but the verb
// exists now so the workflow step it backs is real, not commented out.
if (args is ["migrate-and-seed"])
{
    Console.WriteLine("migrate-and-seed: no migrations yet (XGArcade.Data lands in S-003) — nothing to do.");
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
