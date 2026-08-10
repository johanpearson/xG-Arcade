using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using XGArcade.Api.Auth;
using XGArcade.Core.Auth;

namespace XGArcade.Api.CompositionRoot;

// CORS, request rate limiting, and JWT/Supabase authentication — extracted
// out of Program.cs (S-102) as a pure reorganization, no behavior change.
public static class AuthSetup
{
    // REQ-606/REQ-717: the partition key the auth-signup/auth-login/auth-guest
    // rate-limit policies below key their per-IP counters on. TestServer (WebApplicationFactory)
    // leaves Connection.RemoteIpAddress null, so every request in a given test
    // host collapses onto the same "unknown" partition — that's fine, it's what
    // makes AuthEndpointTests.cs's REQ606 tests able to trip the limit
    // deterministically with a same-process burst of requests rather than
    // needing a real distinct client IP or a mocked clock.
    private static string GetClientIpPartitionKey(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static void ConfigureCorsAndRateLimiting(this WebApplicationBuilder builder)
    {
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

        // REQ-606: rate limiting scoped narrowly to POST /auth/signup and
        // POST /auth/login (AuthController's [EnableRateLimiting("auth-signup"/
        // "auth-login")] attributes below) — not every endpoint, per REQ-606's own
        // scoping. REQ-717/ADR-0036 added a third, POST /auth/guest ("auth-guest").
        // Three separate named policies so exhausting one endpoint's limit never
        // blocks the others. Partitioned per client IP (GetClientIpPartitionKey
        // below): a fixed 1-minute window, no queueing — a request over
        // the limit is rejected immediately with 429 (OnRejected/RejectionStatusCode
        // below), never silently queued or left to fall through as a generic 500.
        // Uses ASP.NET Core's built-in Microsoft.AspNetCore.RateLimiting middleware
        // (available since .NET 7, part of the shared framework) — no new package.
        // Configurable rather than a bare literal: REQ-606 fixes signup/login's
        // production value at 10/min, but ci.yml's E2E job runs the whole Playwright
        // suite (signup + auto-login per test, across every spec file) against one
        // shared backend process from a single CI-runner IP within the same
        // fixed window — a fundamentally different traffic shape than the
        // abuse scenario REQ-606 targets. ci.yml overrides both signup/login values
        // via RateLimiting__AuthSignupPermitLimit/AuthLoginPermitLimit env vars for
        // that job only; every other environment (including local dev) falls
        // back to REQ-606's specified 10, unchanged.
        var authSignupPermitLimit = builder.Configuration.GetValue<int?>("RateLimiting:AuthSignupPermitLimit") ?? 10;
        var authLoginPermitLimit = builder.Configuration.GetValue<int?>("RateLimiting:AuthLoginPermitLimit") ?? 10;
        // REQ-717/ADR-0036: deliberately tighter than signup/login's 10/min default
        // — an anonymous sign-in has no email step at all to slow down scripting
        // (not even a plausible-looking address to type), making it the cheapest
        // identity to mint at scale of the three endpoints here; a real person
        // retrying a flaky network call a couple of times is still comfortably
        // inside 3/min, while a scripted loop is capped far below what 10/min would
        // allow. Same override mechanism as the other two
        // (RateLimiting:AuthGuestPermitLimit) if this default ever needs tuning —
        // ci.yml doesn't currently exercise POST /auth/guest at all (no frontend
        // guest flow yet), so it needs no override today; add one the same way as
        // the other two the moment an E2E spec starts calling this endpoint.
        var authGuestPermitLimit = builder.Configuration.GetValue<int?>("RateLimiting:AuthGuestPermitLimit") ?? 3;

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Errors as problem-details (docs/coding-guidelines.md): the framework's
            // own rejection response has no body by default, so this gives the
            // frontend the same {title, detail} shape every other error response
            // uses (AuthScreen.tsx's describeError already reads exactly this shape,
            // no special-casing needed there).
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.ContentType = "application/problem+json";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new
                    {
                        title = "Too many attempts",
                        detail = "Too many attempts. Please wait a minute and try again.",
                        status = StatusCodes.Status429TooManyRequests,
                    },
                    cancellationToken);
            };

            options.AddPolicy("auth-signup", httpContext => RateLimitPartition.GetFixedWindowLimiter(
                GetClientIpPartitionKey(httpContext),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = authSignupPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));

            options.AddPolicy("auth-login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
                GetClientIpPartitionKey(httpContext),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = authLoginPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));

            options.AddPolicy("auth-guest", httpContext => RateLimitPartition.GetFixedWindowLimiter(
                GetClientIpPartitionKey(httpContext),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = authGuestPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });
    }

    public static void ConfigureSupabaseAuthentication(this WebApplicationBuilder builder)
    {
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
            // REQ-710/ADR-0026: a separate, more-privileged key — never the anon key
            // above — required only for SupabaseAuthClient.DeleteUserAsync's call to
            // Supabase's Admin API. Registered as its own tiny DI type
            // (SupabaseServiceRoleKey) so it flows into SupabaseAuthClient's
            // constructor via the same AddHttpClient<,> typed-client activation as
            // httpClient itself — see that class's doc comment for why this call
            // doesn't get a second HttpClient. ci.yml's local E2E stack
            // (useLocalE2EAuth above) never constructs a SupabaseAuthClient at all,
            // so it never needs this registered.
            var supabaseServiceRoleKey = builder.Configuration["Supabase:ServiceRoleKey"]
                ?? throw new InvalidOperationException("Supabase:ServiceRoleKey is not configured.");
            builder.Services.AddSingleton(new SupabaseServiceRoleKey(supabaseServiceRoleKey));

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

        // S-012: admin-only endpoints (Admin/AdminEndpoints.cs) check the "Admin"
        // policy below, backed by AdminAuthorizationHandler's Admin__UserIds check —
        // see architecture-document.md's security pipeline and implementation-
        // document.md §4.
        builder.Services.AddSingleton<IAuthorizationHandler, AdminAuthorizationHandler>();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("Admin", policy => policy.Requirements.Add(new AdminRequirement()));
        });
    }

    // ADR-0017's rollout-risk mitigation: fires unconditionally at boot, before
    // anyone can even attempt to log in, so the very first thing visible in the
    // log stream after a deploy is the resolved JWKS address — if the path is
    // wrong, that's visible within seconds of checking, not after a confused
    // user reports a login failure. Re-derives useLocalE2EAuth/jwksPath from
    // configuration rather than threading them through from
    // ConfigureSupabaseAuthentication above — both are cheap config reads, and
    // this keeps the builder-time and app-time steps independently callable.
    public static void LogJwksConfiguration(this WebApplication app)
    {
        var useLocalE2EAuth = app.Configuration["Auth:Mode"] == "local-e2e" && app.Environment.IsDevelopment();
        if (useLocalE2EAuth)
            return;

        // Safe: ConfigureSupabaseAuthentication's non-local-e2e branch already did
        // `?? throw` on this exact key — if we reached here, it was present.
        var configuredSupabaseUrl = app.Configuration["Supabase:Url"]!;
        var configuredJwksPath = app.Configuration["Auth:SupabaseJwksPath"] ?? "/auth/v1/.well-known/jwks.json";
        app.Logger.LogInformation(
            "JWT validation configured against Supabase JWKS endpoint {JwksAddress}.",
            configuredSupabaseUrl.TrimEnd('/') + configuredJwksPath);
    }
}
