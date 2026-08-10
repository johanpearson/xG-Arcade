using XGArcade.Api.CompositionRoot;

// S-102: Program.cs is a thin composition root — CLI-verb dispatch,
// DI/auth/CORS/rate-limiting setup, and Minimal-API endpoint mapping each
// live in their own focused extension-method group under CompositionRoot/,
// called from here. See CompositionRoot/*.cs for the moved logic and
// comments; this file only sequences the calls.
if (await CliVerbDispatcher.TryHandleAsync(args))
    return;

var builder = WebApplication.CreateBuilder(args);

builder.ConfigureCorsAndRateLimiting();
builder.AddApplicationServices();
builder.ConfigureSupabaseAuthentication();

builder.Services.AddControllers();

var app = builder.Build();

app.LogJwksConfiguration();
app.ConfigurePipeline();

app.Run();

// Marker partial (global namespace, matching the compiler-generated Program
// class from the top-level statements above) so WebApplicationFactory<Program>
// in XGArcade.Api.Tests can reference it across the assembly boundary.
public partial class Program;
