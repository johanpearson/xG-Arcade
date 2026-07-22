using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Auth;
using XGArcade.Core.Auth;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Tests;

// S-004 (docs/backlog.md): API-level tests for auth/signup, auth/login, and
// the auth/me protected endpoint, scoped to REQ-701's checkbox clause only
// (REQ-702 through REQ-705 — confirmation flow — and REQ-606 rate limiting
// are deferred per MVP-SCOPE.md; not covered here). Never hits a real
// Postgres database or a real Supabase project: the DbContext is swapped for
// an in-memory provider and ISupabaseAuthClient is swapped for a fake test
// double, both via WithWebHostBuilder, same pattern as
// XGArcade.Data.Tests/PlayerStoreRepositoryTests.cs uses for the in-memory
// DB half.
public class AuthEndpointTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;
    private FakeSupabaseAuthClient _fakeAuthClient = null!;

    [SetUp]
    public void SetUp()
    {
        _fakeAuthClient = new FakeSupabaseAuthClient();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Program.cs's real-Supabase JWT validation branch now
                // fetches a live JWKS document (ADR-0017) — unit/API tests
                // must never depend on live network (docs/coding-
                // guidelines.md), so this test host uses the same in-process
                // HS256 signer/validator ci.yml's local E2E stack uses
                // instead. The test host's environment is already
                // Development by default, satisfying local-e2e's other
                // gating condition for free.
                builder.UseSetting("Auth:Mode", "local-e2e");

                builder.ConfigureServices(services =>
                {
                    // Swap the real Npgsql-backed XGArcadeDbContext for a
                    // fresh in-memory one, unique per test. Removing only
                    // DbContextOptions<XGArcadeDbContext>/XGArcadeDbContext
                    // isn't enough: AddDbContext also registers the
                    // configuration action itself as an internal enumerable
                    // service closed over XGArcadeDbContext, so that multiple
                    // AddDbContext calls for the same context compose — left
                    // in place, Program.cs's original UseNpgsql(...) action
                    // would still run alongside ours, and EF Core rejects two
                    // providers configured on one DbContextOptions ("Only a
                    // single database provider can be registered"). So sweep
                    // out every service descriptor closed over
                    // XGArcadeDbContext rather than naming that internal type
                    // directly (it isn't a stable public API to reference).
                    var xgArcadeDbContextDescriptors = services
                        .Where(d => d.ServiceType == typeof(XGArcadeDbContext)
                            || (d.ServiceType.IsGenericType && d.ServiceType.GetGenericArguments().Contains(typeof(XGArcadeDbContext))))
                        .ToList();
                    foreach (var descriptor in xgArcadeDbContextDescriptors)
                    {
                        services.Remove(descriptor);
                    }

                    // Captured once, outside the lambda: AddDbContext invokes
                    // this configure action fresh for every scope, so calling
                    // Guid.NewGuid() inside it would give each scope (the
                    // request's own scope vs. a test's follow-up
                    // CreateScope()) a different in-memory database — they'd
                    // never see each other's writes.
                    var inMemoryDatabaseName = Guid.NewGuid().ToString();
                    services.AddDbContext<XGArcadeDbContext>(options =>
                        options.UseInMemoryDatabase(inMemoryDatabaseName));

                    // Swap whichever ISupabaseAuthClient Program.cs registered
                    // (LocalE2EAuthClient, per Auth:Mode=local-e2e set above)
                    // for a controllable fake — this test suite wants to
                    // dictate Supabase's response per-test (e.g. "signup
                    // rejected"), not exercise the real local-e2e stand-in.
                    services.RemoveAll<ISupabaseAuthClient>();
                    services.AddSingleton<ISupabaseAuthClient>(_fakeAuthClient);
                });
            });
    }

    [TearDown]
    public void TearDown()
    {
        _factory.Dispose();
    }

    [Test]
    public async Task REQ701_Signup_BlockedWithoutAgeConfirmedCheckbox()
    {
        var client = _factory.CreateClient();
        var request = new SignupRequest("unconfirmed-age@example.com", "a-reasonable-password", "a-reasonable-password", "Test Player", AgeConfirmed: false);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        // The important assertion: the checkbox is enforced before any
        // identity is created anywhere, not just before a User row is saved.
        Assert.That(_fakeAuthClient.SignUpCalled, Is.False);
    }

    [Test]
    public async Task REQ701_Signup_BlockedWithMismatchedConfirmPassword()
    {
        var client = _factory.CreateClient();
        var request = new SignupRequest("mismatched-password@example.com", "a-reasonable-password", "a-different-password", "Test Player", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        // The important assertion: the mismatch is enforced before any
        // identity is created anywhere, not just before a User row is saved.
        Assert.That(_fakeAuthClient.SignUpCalled, Is.False);
    }

    // REQ-701 password policy: minimum 8 characters, no forced complexity
    // rules — checked before Supabase Auth is ever called, same discipline
    // as the checkbox/confirm-password checks above.
    [Test]
    public async Task REQ701_Signup_BlockedWithPasswordUnder8Characters()
    {
        var client = _factory.CreateClient();
        var request = new SignupRequest("short-password@example.com", "short12", "short12", "Test Player", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        // The important assertion: the length check is enforced before any
        // identity is created anywhere, not just before a User row is saved.
        Assert.That(_fakeAuthClient.SignUpCalled, Is.False);
    }

    // Exact lower boundary (the valid edge): the policy rejects only
    // length < 8, so 8 characters exactly must be accepted. Pairs with
    // REQ701_Signup_BlockedWithPasswordUnder8Characters above, which covers
    // the 7-character (invalid) side of the same boundary.
    [Test]
    public async Task REQ701_Signup_SucceedsWithPasswordExactly8Characters()
    {
        var client = _factory.CreateClient();
        var request = new SignupRequest("eight-char-password@example.com", "eightchr", "eightchr", "Test Player", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(_fakeAuthClient.SignUpCalled, Is.True);
    }

    // REQ-701 account-enumeration-safe error: attempting to register an
    // email that already has an account must return a generic error whose
    // text never confirms or denies the account's existence.
    [Test]
    public async Task REQ701_Signup_ReturnsGenericEnumerationSafeError_WhenSupabaseRejectsAsAlreadyRegistered()
    {
        _fakeAuthClient.SignUpResult = (_, _) => new SupabaseAuthResult { Success = false, ErrorMessage = "User already registered" };
        var client = _factory.CreateClient();
        var request = new SignupRequest("already-registered-2@example.com", "a-reasonable-password", "a-reasonable-password", "Test Player", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await response.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        Assert.That(body, Is.Not.Null);
        // Supabase's own "already registered" wording must never reach the
        // client — that's exactly what would confirm the account exists.
        Assert.That(body!.Detail, Does.Not.Contain("already registered"));
        Assert.That(body.Detail, Does.Not.Contain("exists"));
        Assert.That(body.Detail, Is.EqualTo("Check your email to confirm your account, or reset your password if you already have one."));
    }

    // The important assertion for enumeration-safety: a completely different
    // Supabase rejection reason produces the exact same response body as the
    // already-registered case above. If the two cases were distinguishable
    // in any way, that difference itself would leak which one occurred.
    [Test]
    public async Task REQ701_Signup_ReturnsSameGenericError_RegardlessOfSupabaseRejectionReason()
    {
        _fakeAuthClient.SignUpResult = (_, _) => new SupabaseAuthResult { Success = false, ErrorMessage = "Unable to validate email address: invalid format" };
        var client = _factory.CreateClient();
        var request = new SignupRequest("some-other-rejection-reason@example.com", "a-reasonable-password", "a-reasonable-password", "Test Player", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await response.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Detail, Is.EqualTo("Check your email to confirm your account, or reset your password if you already have one."));
    }

    [Test]
    public async Task REQ701_Signup_SucceedsWithAgeConfirmedCheckbox()
    {
        var client = _factory.CreateClient();
        var request = new SignupRequest("confirmed-age@example.com", "a-reasonable-password", "a-reasonable-password", "Test Player", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(_fakeAuthClient.SignUpCalled, Is.True);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = await dbContext.Users.SingleOrDefaultAsync(u => u.Email == "confirmed-age@example.com");
        Assert.That(user, Is.Not.Null);
        Assert.That(user!.DisplayName, Is.EqualTo("Test Player"));
    }

    // S-017: case-insensitive DisplayName uniqueness (REQ-701/REQ-401).
    // These exercise AuthController.Signup's IUserRepository
    // .DisplayNameExistsAsync pre-check only — the in-memory EF Core
    // provider this test host uses (see SetUp) does not enforce the real
    // unique index (IX_Users_NormalizedDisplayName), so the DbUpdateException
    // /DisplayNameAlreadyInUseException race-condition catch in
    // UserRepository.AddAsync can't be exercised at this level. That path is
    // covered only by the manual verification against a real local Postgres
    // instance noted in docs/backlog.md's S-017 entry — see also
    // XGArcade.Data.Tests/UserRepositoryTests.cs for why it can't be
    // meaningfully unit-tested with InMemory either.
    [Test]
    public async Task REQ701_Signup_BlockedWhenDisplayNameExactlyMatchesExistingUser()
    {
        using (var seedScope = _factory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = Guid.NewGuid(),
                Email = "existing-name-owner@example.com",
                DisplayName = "Test Player",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var request = new SignupRequest("new-signup-same-name@example.com", "a-reasonable-password", "a-reasonable-password", "Test Player", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        // The important assertion: the display name collision is caught
        // before any identity is created anywhere, not just before a User
        // row is saved.
        Assert.That(_fakeAuthClient.SignUpCalled, Is.False);
    }

    [Test]
    public async Task REQ701_Signup_BlockedWhenDisplayNameMatchesExistingUserOnlyInCasing()
    {
        using (var seedScope = _factory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = Guid.NewGuid(),
                Email = "existing-name-owner-2@example.com",
                DisplayName = "Test Player",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var request = new SignupRequest("new-signup-different-casing@example.com", "a-reasonable-password", "a-reasonable-password", "TEST PLAYER", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(_fakeAuthClient.SignUpCalled, Is.False);
    }

    // REQ-701's uniqueness check runs against the already-trimmed
    // DisplayName (see Signup's existing `request.DisplayName.Trim()`), so a
    // leading/trailing-whitespace variant of an existing name must still be
    // caught as a collision — not treated as a distinct name.
    [Test]
    public async Task REQ701_Signup_BlockedWhenDisplayNameMatchesExistingUserAfterTrimming()
    {
        using (var seedScope = _factory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = Guid.NewGuid(),
                Email = "existing-name-owner-4@example.com",
                DisplayName = "Test Player",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var request = new SignupRequest("new-signup-untrimmed@example.com", "a-reasonable-password", "a-reasonable-password", "  Test Player  ", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(_fakeAuthClient.SignUpCalled, Is.False);
    }

    [Test]
    public async Task REQ701_Signup_LeavesExistingUsersDisplayNameUnchanged_AfterFailedCollidingSignup()
    {
        var existingUserId = Guid.NewGuid();
        using (var seedScope = _factory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = existingUserId,
                AuthProviderUserId = Guid.NewGuid(),
                Email = "existing-name-owner-3@example.com",
                DisplayName = "Test Player",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var request = new SignupRequest("new-signup-should-fail@example.com", "a-reasonable-password", "a-reasonable-password", "test player", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var existingUser = await assertDbContext.Users.AsNoTracking().SingleAsync(u => u.Id == existingUserId);
        Assert.That(existingUser.DisplayName, Is.EqualTo("Test Player"));
        // No second row was created for the rejected signup's email either.
        var rejectedSignupUser = await assertDbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == "new-signup-should-fail@example.com");
        Assert.That(rejectedSignupUser, Is.Null);
    }

    // Not REQ-701-named: this exercises Supabase's own rejection (e.g.
    // duplicate email) surfacing as a client error, not the checkbox clause
    // REQ-701 is narrowed to for this story (docs/backlog.md S-004).
    [Test]
    public async Task Signup_Post_ReturnsBadRequest_WhenSupabaseAuthRejectsSignup()
    {
        _fakeAuthClient.SignUpResult = (_, _) => new SupabaseAuthResult { Success = false, ErrorMessage = "User already registered" };
        var client = _factory.CreateClient();
        var request = new SignupRequest("already-registered@example.com", "a-reasonable-password", "a-reasonable-password", "Test Player", AgeConfirmed: true);

        var response = await client.PostAsJsonAsync("/auth/signup", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Login_Post_ReturnsAccessToken_ForValidCredentials()
    {
        _fakeAuthClient.SignInResult = (_, _) => new SupabaseAuthResult
        {
            Success = true,
            AuthProviderUserId = Guid.NewGuid(),
            AccessToken = "a-fake-access-token",
            RefreshToken = "a-fake-refresh-token",
        };
        var client = _factory.CreateClient();
        var request = new LoginRequest("known-user@example.com", "a-reasonable-password");

        var response = await client.PostAsJsonAsync("/auth/login", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.AccessToken, Is.EqualTo("a-fake-access-token"));
        Assert.That(body.RefreshToken, Is.EqualTo("a-fake-refresh-token"));
    }

    [Test]
    public async Task Login_Post_ReturnsUnauthorized_WhenSupabaseAuthRejectsCredentials()
    {
        _fakeAuthClient.SignInResult = (_, _) => new SupabaseAuthResult { Success = false, ErrorMessage = "Invalid login credentials" };
        var client = _factory.CreateClient();
        var request = new LoginRequest("known-user@example.com", "the-wrong-password");

        var response = await client.PostAsJsonAsync("/auth/login", request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // REQ-606: rate limiting scoped to POST /auth/signup (Program.cs's
    // "auth-signup" fixed-window policy — 10 requests/minute per IP). The
    // password-policy/confirm-password/age-checkbox checks all run inside
    // the controller action, but the rate limiter middleware counts every
    // request that reaches the route regardless of what the action itself
    // returns — so a fast, deliberately-invalid request (age checkbox
    // unchecked, always a cheap 400) is enough to exercise the limiter
    // without 10+ real signups. WebApplicationFactory's TestServer leaves
    // Connection.RemoteIpAddress null, so every request from this test's
    // single client collapses onto the same partition (see Program.cs's
    // GetClientIpPartitionKey), making this deterministic without waiting
    // out a real window or mocking the clock.
    [Test]
    public async Task REQ606_Signup_ReturnsTooManyRequests_AfterExceedingPerMinuteLimit()
    {
        var client = _factory.CreateClient();

        HttpResponseMessage? lastWithinLimitResponse = null;
        for (var i = 0; i < 10; i++)
        {
            lastWithinLimitResponse = await client.PostAsJsonAsync(
                "/auth/signup",
                new SignupRequest($"rate-limit-signup-{i}@example.com", "a-reasonable-password", "a-reasonable-password", "Test Player", AgeConfirmed: false));
        }
        Assert.That(lastWithinLimitResponse!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "The 10th request in the window should still be processed normally.");

        var overLimitResponse = await client.PostAsJsonAsync(
            "/auth/signup",
            new SignupRequest("rate-limit-signup-11@example.com", "a-reasonable-password", "a-reasonable-password", "Test Player", AgeConfirmed: false));

        Assert.That(overLimitResponse.StatusCode, Is.EqualTo((HttpStatusCode)429));
        var body = await overLimitResponse.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Title, Is.EqualTo("Too many attempts"));
    }

    // Same as above but for POST /auth/login (Program.cs's "auth-login"
    // policy, a separate counter from "auth-signup" above).
    [Test]
    public async Task REQ606_Login_ReturnsTooManyRequests_AfterExceedingPerMinuteLimit()
    {
        _fakeAuthClient.SignInResult = (_, _) => new SupabaseAuthResult { Success = false, ErrorMessage = "Invalid login credentials" };
        var client = _factory.CreateClient();

        HttpResponseMessage? lastWithinLimitResponse = null;
        for (var i = 0; i < 10; i++)
        {
            lastWithinLimitResponse = await client.PostAsJsonAsync(
                "/auth/login",
                new LoginRequest("rate-limit-login@example.com", "the-wrong-password"));
        }
        Assert.That(lastWithinLimitResponse!.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "The 10th request in the window should still be processed normally.");

        var overLimitResponse = await client.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest("rate-limit-login@example.com", "the-wrong-password"));

        Assert.That(overLimitResponse.StatusCode, Is.EqualTo((HttpStatusCode)429));
        var body = await overLimitResponse.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Title, Is.EqualTo("Too many attempts"));
    }

    // The important assertion: signup and login are rate-limited
    // independently (two named policies in Program.cs, not one shared
    // counter) — exhausting one endpoint's limit must never block the
    // other.
    [Test]
    public async Task REQ606_ExhaustingSignupRateLimit_DoesNotAffectLogin()
    {
        var client = _factory.CreateClient();

        for (var i = 0; i < 11; i++)
        {
            await client.PostAsJsonAsync(
                "/auth/signup",
                new SignupRequest($"rate-limit-isolation-{i}@example.com", "a-reasonable-password", "a-reasonable-password", "Test Player", AgeConfirmed: false));
        }

        // Sanity check: signup really is exhausted at this point.
        var signupResponse = await client.PostAsJsonAsync(
            "/auth/signup",
            new SignupRequest("rate-limit-isolation-confirm@example.com", "a-reasonable-password", "a-reasonable-password", "Test Player", AgeConfirmed: false));
        Assert.That(signupResponse.StatusCode, Is.EqualTo((HttpStatusCode)429));

        // Login uses a separate policy/counter, so it's still unaffected.
        var loginResponse = await client.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest("known-user@example.com", "a-reasonable-password"));
        Assert.That(loginResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // REQ-715: POST /auth/refresh exchanges a stored refresh token for a new
    // access token, mediated through Supabase Auth (ADR-0013) — never a
    // direct frontend-to-Supabase call. No [Authorize] on this endpoint (the
    // caller may not have a currently-valid access token at all), so these
    // tests don't set an Authorization header.
    [Test]
    public async Task REQ715_Refresh_Post_ReturnsNewAccessToken_ForValidRefreshToken()
    {
        _fakeAuthClient.RefreshResult = _ => new SupabaseAuthResult
        {
            Success = true,
            AuthProviderUserId = Guid.NewGuid(),
            AccessToken = "a-refreshed-access-token",
            RefreshToken = "a-rotated-refresh-token",
        };
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest("a-valid-stored-refresh-token"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.AccessToken, Is.EqualTo("a-refreshed-access-token"));
        Assert.That(body.RefreshToken, Is.EqualTo("a-rotated-refresh-token"));
        Assert.That(_fakeAuthClient.RefreshTokenCalledWith, Is.EqualTo("a-valid-stored-refresh-token"));
    }

    // The important assertion: an invalid/expired/revoked refresh token
    // fails clearly and distinctly (401, ProblemDetails body) — never a
    // generic 500, never left to fall through as an unhandled exception —
    // so the frontend can react by signing the person out.
    [Test]
    public async Task REQ715_Refresh_Post_ReturnsUnauthorized_ForInvalidRefreshToken()
    {
        _fakeAuthClient.RefreshResult = _ => new SupabaseAuthResult { Success = false, ErrorMessage = "Invalid Refresh Token: Already Used" };
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/refresh", new RefreshRequest("an-invalid-or-revoked-refresh-token"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- REQ-717/ADR-0036: guest play ----

    [Test]
    public async Task REQ717_Guest_Post_ReturnsAccessAndRefreshToken_AndCreatesGuestUserRowWithNoEmail()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/guest", new GuestRequest("a-fake-turnstile-token"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.AccessToken, Is.EqualTo("a-fake-guest-access-token"));
        Assert.That(body.RefreshToken, Is.EqualTo("a-fake-guest-refresh-token"));
        Assert.That(_fakeAuthClient.SignInAnonymouslyCalled, Is.True);
        // REQ-717's 2026-07-21 "Bot-check (captcha)" addition / ADR-0037:
        // the Turnstile token is forwarded to Supabase unmodified, never
        // checked or altered by this backend.
        Assert.That(_fakeAuthClient.SignInAnonymouslyCalledWithCaptchaToken, Is.EqualTo("a-fake-turnstile-token"));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = await dbContext.Users.SingleAsync();
        Assert.That(user.IsGuest, Is.True);
        Assert.That(user.Email, Is.Null);
        Assert.That(user.ClaimedAt, Is.Null);
    }

    // REQ-717's 2026-07-21 "Bot-check (captcha)" addition / ADR-0037: a
    // missing Turnstile token must produce a distinct, specific rejection —
    // never the generic "Guest sign-in failed" this endpoint returns for
    // its other failure modes — so the frontend can reset the widget and
    // retry rather than treating it like any other opaque failure. Modeled
    // here as Supabase itself rejecting the (empty/missing) token — this
    // backend never verifies the token independently (ADR-0037) — the same
    // way REQ717_Guest_Post_ReturnsDistinctCaptchaRejection_WhenSupabaseRejectsTheToken
    // below models an explicitly-invalid one; both go through the same
    // IsCaptchaRejection signal.
    [Test]
    public async Task REQ717_Guest_Post_ReturnsDistinctCaptchaRejection_WhenCaptchaTokenIsMissing()
    {
        _fakeAuthClient.SignInAnonymouslyResult = _ =>
            new SupabaseAuthResult { Success = false, ErrorMessage = "captcha verification process failed", IsCaptchaRejection = true };
        var client = _factory.CreateClient();

        // No CaptchaToken supplied at all — GuestRequest's CaptchaToken
        // binds to null, forwarded to Supabase unmodified per ADR-0037's
        // pass-through decision (no local pre-check of the token's presence
        // in this controller).
        var response = await client.PostAsJsonAsync("/auth/guest", new { });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await response.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        Assert.That(body, Is.Not.Null);
        // The important assertion: distinct from the generic "Guest sign-in
        // failed" title every other Guest failure mode returns.
        Assert.That(body!.Title, Is.EqualTo("Captcha verification failed"));
        Assert.That(body.Title, Is.Not.EqualTo("Guest sign-in failed"));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.Users.AnyAsync(), Is.False, "no User row should be created for a rejected guest sign-in");
    }

    // REQ-717's 2026-07-21 "Bot-check (captcha)" addition / ADR-0037: a
    // present-but-invalid/expired Turnstile token, rejected by Supabase's
    // own captcha verification, must produce the same distinct rejection as
    // a missing one above — the frontend reacts identically either way
    // (reset the widget, get a fresh token, retry).
    [Test]
    public async Task REQ717_Guest_Post_ReturnsDistinctCaptchaRejection_WhenSupabaseRejectsTheToken()
    {
        _fakeAuthClient.SignInAnonymouslyResult = _ =>
            new SupabaseAuthResult { Success = false, ErrorMessage = "captcha protection: request disallowed (not-a-robot)", IsCaptchaRejection = true };
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/guest", new GuestRequest("an-expired-or-invalid-turnstile-token"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await response.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Title, Is.EqualTo("Captcha verification failed"));
        Assert.That(_fakeAuthClient.SignInAnonymouslyCalledWithCaptchaToken, Is.EqualTo("an-expired-or-invalid-turnstile-token"));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.Users.AnyAsync(), Is.False, "no User row should be created for a rejected guest sign-in");
    }

    // REQ-717/ADR-0036: every other Guest sign-in rejection (i.e. not a
    // captcha rejection) must still return the pre-existing generic
    // "Guest sign-in failed" response, unchanged — this ADR-0037 addition
    // only carves out captcha rejections specifically, it doesn't touch
    // any other failure mode's response shape.
    [Test]
    public async Task REQ717_Guest_Post_ReturnsGenericGuestSignInFailed_ForNonCaptchaRejection()
    {
        _fakeAuthClient.SignInAnonymouslyResult = _ =>
            new SupabaseAuthResult { Success = false, ErrorMessage = "Anonymous sign-ins are disabled." };
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/guest", new GuestRequest("a-fake-turnstile-token"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        var body = await response.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Title, Is.EqualTo("Guest sign-in failed"));
    }

    // REQ-717: "Guest####-style" default display name, satisfying REQ-701's
    // existing 1-30 character bound.
    [Test]
    public async Task REQ717_Guest_Post_GeneratesDefaultDisplayNameMatchingGuestPattern()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/guest", new GuestRequest("a-fake-turnstile-token"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = await dbContext.Users.SingleAsync();
        Assert.That(user.DisplayName, Does.StartWith("Guest"));
        Assert.That(user.DisplayName.Length, Is.LessThanOrEqualTo(30));
    }

    // REQ-401/REQ-717: a guest is auto-enrolled in the Global league exactly
    // like any other new account, through the same ordinary LeagueMembership
    // mechanism — no second, guest-specific enrollment path.
    [Test]
    public async Task REQ717_Guest_Post_EnrollsGuestInGlobalLeague()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/guest", new GuestRequest("a-fake-turnstile-token"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = await dbContext.Users.SingleAsync();
        var membership = await dbContext.LeagueMemberships.SingleOrDefaultAsync(m => m.UserId == user.Id);
        Assert.That(membership, Is.Not.Null);
    }

    // REQ-717: AuthController.GenerateUniqueGuestDisplayNameAsync retries
    // its candidate up to 10 times against DisplayNameExistsAsync before
    // falling back to a longer, Guid-derived name. This test exercises the
    // full-exhaustion end of that behavior: it makes EVERY one of the 9000
    // possible "GuestNNNN" candidates (1000-9999 inclusive, the exact range
    // GenerateUniqueGuestDisplayNameAsync draws from) already taken —
    // guaranteeing all 10 retry attempts collide regardless of which values
    // are drawn — and pins down the resulting fallback: a longer,
    // Guid-derived name, still unique and within REQ-701's 30-character
    // bound, rather than an infinite loop or a duplicate row. See
    // REQ717_Guest_Post_RetriesOnce_WhenFirstGuestNNNNCandidateCollides_ThenSucceedsWithTheSecond
    // below for the "collides once, then succeeds on retry" case, which
    // needs the controllable-Random seam (AuthController's optional
    // `Random random = null` constructor param) this test predates.
    [Test]
    public async Task REQ717_Guest_Post_FallsBackToGuidDerivedDisplayName_WhenEveryGuestNNNNCandidateIsAlreadyTaken()
    {
        using (var seedScope = _factory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            var takenNames = new List<User>();
            for (var candidate = 1000; candidate < 10000; candidate++)
            {
                takenNames.Add(new User
                {
                    Id = Guid.NewGuid(),
                    AuthProviderUserId = Guid.NewGuid(),
                    Email = $"{Guid.NewGuid()}@example.com",
                    DisplayName = $"Guest{candidate}",
                    EmailConfirmed = true,
                    CreatedAt = DateTime.UtcNow,
                });
            }
            dbContext.Users.AddRange(takenNames);
            await dbContext.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/guest", new GuestRequest("a-fake-turnstile-token"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var guest = await assertDbContext.Users.AsNoTracking().SingleAsync(u => u.IsGuest);
        // None of the 9000 "GuestNNNN" candidates were available, so the
        // Guid-derived fallback must have been used instead of the short
        // random-4-digit form every other guest in this file gets.
        Assert.That(guest.DisplayName, Does.StartWith("Guest"));
        Assert.That(guest.DisplayName, Does.Not.Match(@"^Guest\d{4}$"));
        Assert.That(guest.DisplayName.Length, Is.LessThanOrEqualTo(30));
    }

    // REQ-717: the retry path itself — the first "GuestNNNN" candidate
    // collides against a pre-seeded row, and the second (distinct) draw
    // succeeds. Only possible deterministically because of the seam added
    // alongside this test: AuthController's optional `Random random = null`
    // constructor param (same pattern GridGameModule already uses for its
    // own Random.Shared dependency — see that class's own comment), swapped
    // here for SequentialCandidateRandom below via the same
    // WithWebHostBuilder/AddSingleton idiom
    // LeagueEndpointTests.REQ402_PostLeagues_EveryGeneratedInviteCodeCollides_SurfacesAsUnhandled500
    // already uses to override one dependency for one test. Complements
    // (does not replace) the full-exhaustion test above.
    [Test]
    public async Task REQ717_Guest_Post_RetriesOnce_WhenFirstGuestNNNNCandidateCollides_ThenSucceedsWithTheSecond()
    {
        // WithWebHostBuilder builds an entirely separate host/in-memory
        // database from _factory's own (SetUp's ConfigureServices runs
        // again, generating a fresh database name) — so the pre-existing
        // colliding row must be seeded into THIS factory's database, not
        // _factory's.
        var deterministicFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Nothing registers a Random today, so AuthController's
                // optional constructor param falls back to Random.Shared —
                // registering one here is enough to override it, no
                // RemoveAll needed.
                services.AddSingleton<Random>(new SequentialCandidateRandom(5000, 5001));
            });
        });

        using (var seedScope = deterministicFactory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = Guid.NewGuid(),
                Email = $"{Guid.NewGuid()}@example.com",
                DisplayName = "Guest5000",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var client = deterministicFactory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/guest", new GuestRequest("a-fake-turnstile-token"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var assertScope = deterministicFactory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var guest = await assertDbContext.Users.AsNoTracking().SingleAsync(u => u.IsGuest);
        Assert.That(guest.DisplayName, Is.EqualTo("Guest5001"),
            "the first candidate (Guest5000) must have collided against the pre-seeded row and been retried, landing on the second (Guest5001) — the retry path itself, not just the full-exhaustion fallback");
    }

    // REQ-717: a deterministic stand-in for Random.Shared, returning a
    // fixed, caller-supplied sequence of Next(int,int) results in order —
    // Random.Next(int,int) has been virtual since .NET 6 specifically to
    // support this kind of test subclass.
    private sealed class SequentialCandidateRandom(params int[] values) : Random
    {
        private int _index;

        public override int Next(int minValue, int maxValue) => values[_index++];
    }

    // REQ-717/ADR-0036: a separate, tighter rate-limit policy than
    // auth-signup/auth-login's 10/min (Program.cs's "auth-guest" —
    // 3/min by default, unless RateLimiting:AuthGuestPermitLimit overrides
    // it, which this test host doesn't). Same deterministic single-client
    // burst idiom as REQ606_Signup/Login above.
    [Test]
    public async Task REQ717_Guest_Post_ReturnsTooManyRequests_AfterExceedingPerMinuteLimit()
    {
        var client = _factory.CreateClient();

        HttpResponseMessage? lastWithinLimitResponse = null;
        for (var i = 0; i < 3; i++)
        {
            lastWithinLimitResponse = await client.PostAsJsonAsync("/auth/guest", new GuestRequest("a-fake-turnstile-token"));
        }
        Assert.That(lastWithinLimitResponse!.StatusCode, Is.EqualTo(HttpStatusCode.OK), "The 3rd request in the window should still be processed normally.");

        var overLimitResponse = await client.PostAsJsonAsync("/auth/guest", new GuestRequest("a-fake-turnstile-token"));

        Assert.That(overLimitResponse.StatusCode, Is.EqualTo((HttpStatusCode)429));
        var body = await overLimitResponse.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Title, Is.EqualTo("Too many attempts"));
    }

    // The important assertion: auth-guest is a separate counter from
    // auth-signup/auth-login — exhausting one never blocks the others.
    [Test]
    public async Task REQ717_ExhaustingGuestRateLimit_DoesNotAffectSignupOrLogin()
    {
        var client = _factory.CreateClient();

        for (var i = 0; i < 4; i++)
        {
            await client.PostAsJsonAsync("/auth/guest", new GuestRequest("a-fake-turnstile-token"));
        }
        var guestResponse = await client.PostAsJsonAsync("/auth/guest", new GuestRequest("a-fake-turnstile-token"));
        Assert.That(guestResponse.StatusCode, Is.EqualTo((HttpStatusCode)429));

        var signupResponse = await client.PostAsJsonAsync(
            "/auth/signup",
            new SignupRequest("guest-rate-limit-isolation@example.com", "a-reasonable-password", "a-reasonable-password", "Test Player", AgeConfirmed: true));
        Assert.That(signupResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        var loginResponse = await client.PostAsJsonAsync(
            "/auth/login",
            new LoginRequest("known-user@example.com", "a-reasonable-password"));
        Assert.That(loginResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // ---- REQ-717/ADR-0036: claim/upgrade path ----

    [Test]
    public async Task REQ717_Claim_Post_ConvertsGuestToRealAccount_ClearsIsGuestAndStampsClaimedAt()
    {
        var authProviderUserId = Guid.NewGuid();
        var guest = await SeedGuestUserAsync(authProviderUserId);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.PostAsJsonAsync("/auth/claim", new ClaimAccountRequest("claimed@example.com", "a-reasonable-password", "a-reasonable-password"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Email, Is.EqualTo("claimed@example.com"));
        Assert.That(body.IsGuest, Is.False);

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var updated = await assertDbContext.Users.AsNoTracking().SingleAsync(u => u.Id == guest.Id);
        Assert.That(updated.IsGuest, Is.False);
        Assert.That(updated.Email, Is.EqualTo("claimed@example.com"));
        Assert.That(updated.ClaimedAt, Is.Not.Null);
    }

    // The important assertion (REQ-717's explicit acceptance criterion):
    // every Guess/LeagueMembership row already attributed to this User.Id
    // survives the claim unchanged — no re-linking, no new rows.
    [Test]
    public async Task REQ717_Claim_Post_PreservesExistingGuessAndLeagueMembershipRowsUnchanged()
    {
        var authProviderUserId = Guid.NewGuid();
        var guest = await SeedGuestUserAsync(authProviderUserId);
        await SeedGuessAsync(guest.Id);
        Guid membershipLeagueId;
        using (var seedScope = _factory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            var league = await dbContext.Leagues.SingleAsync();
            membershipLeagueId = league.Id;
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.PostAsJsonAsync("/auth/claim", new ClaimAccountRequest("claimed-2@example.com", "a-reasonable-password", "a-reasonable-password"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var guesses = await assertDbContext.Guesses.AsNoTracking().ToListAsync();
        Assert.That(guesses, Has.Count.EqualTo(1));
        Assert.That(guesses.Single().UserId, Is.EqualTo(guest.Id));
        var memberships = await assertDbContext.LeagueMemberships.AsNoTracking().Where(m => m.UserId == guest.Id).ToListAsync();
        Assert.That(memberships, Has.Count.EqualTo(1));
        Assert.That(memberships.Single().LeagueId, Is.EqualTo(membershipLeagueId));
    }

    [Test]
    public async Task REQ717_Claim_Post_RejectsWhenCallerIsNotAGuest()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedDeletableUserAsync(authProviderUserId);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.PostAsJsonAsync("/auth/claim", new ClaimAccountRequest("already-real@example.com", "a-reasonable-password", "a-reasonable-password"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task REQ717_Claim_Post_RejectsMismatchedConfirmPassword()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedGuestUserAsync(authProviderUserId);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.PostAsJsonAsync("/auth/claim", new ClaimAccountRequest("mismatched@example.com", "a-reasonable-password", "a-different-password"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task REQ717_Claim_Post_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/claim", new ClaimAccountRequest("anyone@example.com", "a-reasonable-password", "a-reasonable-password"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // REQ-717: seeds a guest User row directly (IsGuest = true, no email) —
    // same idiom as SeedDeletableUserAsync below, for the claim-path tests
    // above.
    private async Task<User> SeedGuestUserAsync(Guid authProviderUserId)
    {
        using var seedScope = _factory.Services.CreateScope();
        var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = authProviderUserId,
            Email = null,
            DisplayName = $"Guest{Guid.NewGuid():N}"[..12],
            EmailConfirmed = false,
            IsGuest = true,
            CreatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var leagueRepository = seedScope.ServiceProvider.GetRequiredService<ILeagueRepository>();
        var globalLeague = await leagueRepository.GetOrCreateGlobalLeagueAsync();
        await leagueRepository.AddMembershipAsync(globalLeague.Id, user.Id);

        return user;
    }

    [Test]
    public async Task ProtectedEndpoint_Get_RejectsAnonymousCalls()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/auth/me");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task ProtectedEndpoint_Get_ReturnsProfile_ForAuthenticatedKnownUser()
    {
        var authProviderUserId = Guid.NewGuid();

        using (var seedScope = _factory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = authProviderUserId,
                Email = "known-user@example.com",
                DisplayName = "Known User",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.GetAsync("/auth/me");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Email, Is.EqualTo("known-user@example.com"));
        Assert.That(body.EmailConfirmed, Is.True);
        Assert.That(body.IsGuest, Is.False);
    }

    // REQ-717: GET /auth/me's IsGuest field mirrors User.IsGuest directly —
    // the frontend's first-class replacement for inferring guest status
    // from Email being null.
    [Test]
    public async Task REQ717_Me_Get_ReturnsIsGuestTrue_ForGuestUser()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedGuestUserAsync(authProviderUserId);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.GetAsync("/auth/me");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.IsGuest, Is.True);
        Assert.That(body.Email, Is.Null);
    }

    // REQ-504: GET /auth/me's IsAdmin field — computed via the same
    // AdminAuthorizationHandler.IsAdminUserId helper the "Admin" policy
    // itself uses (Admin:UserIds config), so the frontend can decide whether
    // to render the admin nav entry point. This SetUp's factory doesn't
    // configure Admin:UserIds at all (no other test in this file needs it),
    // so each of these two tests builds its own variant factory via
    // WithWebHostBuilder rather than disturbing SetUp for every other test —
    // same idiom as RoundEndpointTests' throwingFactory/productionFactory.
    [Test]
    public async Task REQ504_Me_Get_ReturnsIsAdminTrue_ForUserInAdminUserIds()
    {
        var authProviderUserId = Guid.NewGuid();
        var adminFactory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Admin:UserIds", authProviderUserId.ToString()));

        using (var seedScope = adminFactory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = authProviderUserId,
                Email = "admin-user@example.com",
                DisplayName = "Admin User",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var client = adminFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.GetAsync("/auth/me");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.IsAdmin, Is.True);
    }

    [Test]
    public async Task REQ504_Me_Get_ReturnsIsAdminFalse_ForUserNotInAdminUserIds()
    {
        var authProviderUserId = Guid.NewGuid();
        // A different GUID in Admin:UserIds — proves the false case isn't
        // just "config is empty", but a genuine non-match against a
        // populated admin list.
        var adminFactory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Admin:UserIds", Guid.NewGuid().ToString()));

        using (var seedScope = adminFactory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = authProviderUserId,
                Email = "non-admin-user@example.com",
                DisplayName = "Non-Admin User",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var client = adminFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.GetAsync("/auth/me");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.IsAdmin, Is.False);
    }

    // REQ-714: edit display name from Settings. Reuses SeedDeletableUserAsync
    // below (a generic "seed one User row, mint a JWT for it" helper — its
    // name is about the other feature it was first written for, not
    // specific to this REQ).
    [Test]
    public async Task REQ714_UpdateDisplayName_Put_UpdatesDisplayName_ForValidNewName()
    {
        var authProviderUserId = Guid.NewGuid();
        var user = await SeedDeletableUserAsync(authProviderUserId, displayName: "Old Name");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.PutAsJsonAsync("/auth/display-name", new UpdateDisplayNameRequest("New Name"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<UpdateDisplayNameResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.DisplayName, Is.EqualTo("New Name"));

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var updatedUser = await assertDbContext.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.That(updatedUser.DisplayName, Is.EqualTo("New Name"));
    }

    // The important assertion (REQ-714's explicit acceptance criterion): a
    // no-op resubmission of the caller's own current name, including a
    // pure-casing change, is never treated as a conflict against itself —
    // the uniqueness check must exclude the account's own existing row.
    [Test]
    public async Task REQ714_UpdateDisplayName_Put_AllowsResubmittingOwnCurrentName_EvenWithDifferentCasing()
    {
        var authProviderUserId = Guid.NewGuid();
        var user = await SeedDeletableUserAsync(authProviderUserId, displayName: "Test Player");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.PutAsJsonAsync("/auth/display-name", new UpdateDisplayNameRequest("TEST PLAYER"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<UpdateDisplayNameResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.DisplayName, Is.EqualTo("TEST PLAYER"));

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var updatedUser = await assertDbContext.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.That(updatedUser.DisplayName, Is.EqualTo("TEST PLAYER"));
    }

    [Test]
    public async Task REQ714_UpdateDisplayName_Put_ReturnsConflict_WhenNameMatchesDifferentAccountCaseInsensitively()
    {
        using (var seedScope = _factory.Services.CreateScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = Guid.NewGuid(),
                Email = "other-account-owner@example.com",
                DisplayName = "Taken Name",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
        }

        var authProviderUserId = Guid.NewGuid();
        var user = await SeedDeletableUserAsync(authProviderUserId, email: "caller@example.com", displayName: "Caller Name");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.PutAsJsonAsync("/auth/display-name", new UpdateDisplayNameRequest("TAKEN NAME"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var unchangedUser = await assertDbContext.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.That(unchangedUser.DisplayName, Is.EqualTo("Caller Name"));
    }

    [Test]
    public async Task REQ714_UpdateDisplayName_Put_ReturnsBadRequest_ForNameOutsideLengthBound()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedDeletableUserAsync(authProviderUserId, displayName: "Original Name");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.PutAsJsonAsync("/auth/display-name", new UpdateDisplayNameRequest(new string('x', 31)));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // Exact upper boundary (the valid edge): AuthController.UpdateDisplayName
    // rejects only length > 30, so 30 characters exactly must be accepted.
    // Pairs with REQ714_UpdateDisplayName_Put_ReturnsBadRequest_ForNameOutsideLengthBound
    // above, which covers the 31-character (invalid) side of the same boundary.
    [Test]
    public async Task REQ714_UpdateDisplayName_Put_Succeeds_ForNameExactly30Characters()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedDeletableUserAsync(authProviderUserId, displayName: "Original Name");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var thirtyCharacterName = new string('x', 30);
        var response = await client.PutAsJsonAsync("/auth/display-name", new UpdateDisplayNameRequest(thirtyCharacterName));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<UpdateDisplayNameResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.DisplayName, Is.EqualTo(thirtyCharacterName));
    }

    [Test]
    public async Task REQ714_UpdateDisplayName_Put_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync("/auth/display-name", new UpdateDisplayNameRequest("Anything"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // REQ-710: self-service account deletion (S-025). Seeds a user directly
    // into the in-memory DB (same pattern as ProtectedEndpoint_Get tests
    // above) and mints its own JWT via LocalE2EAuth.MintToken for the
    // [Authorize]-protected DELETE /auth/account endpoint.
    private async Task<User> SeedDeletableUserAsync(Guid authProviderUserId, string email = "deletable-user@example.com", string? displayName = null)
    {
        using var seedScope = _factory.Services.CreateScope();
        var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = authProviderUserId,
            Email = email,
            DisplayName = displayName ?? $"Player-{Guid.NewGuid():N}",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }

    private async Task SeedGuessAsync(Guid userId)
    {
        using var seedScope = _factory.Services.CreateScope();
        var dbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        dbContext.Guesses.Add(new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = Guid.NewGuid(),
            UserId = userId,
            CellId = Guid.NewGuid(),
            SubmittedName = "Someone",
            IsCorrect = true,
            AttemptCount = 1,
            CreatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    [Test]
    public async Task REQ710_DeleteAccount_CorrectPassword_ReturnsNoContentAndCallsSupabaseDelete()
    {
        var authProviderUserId = Guid.NewGuid();
        var user = await SeedDeletableUserAsync(authProviderUserId);
        // Default FakeSupabaseAuthClient.SignInResult already succeeds — the
        // confirmation step (AuthController.DeleteAccount's re-verification
        // via SignInWithPasswordAsync) passes without any per-test override.

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.SendAsync(BuildDeleteRequest("the-correct-password"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(_fakeAuthClient.DeleteUserCalledWith, Is.EqualTo(authProviderUserId));

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var remainingUser = await assertDbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == user.Id);
        Assert.That(remainingUser, Is.Null);
    }

    [Test]
    public async Task REQ710_DeleteAccount_WrongPassword_Returns401AndDoesNotDeleteAnything()
    {
        var authProviderUserId = Guid.NewGuid();
        var user = await SeedDeletableUserAsync(authProviderUserId);
        _fakeAuthClient.SignInResult = (_, _) => new SupabaseAuthResult { Success = false, ErrorMessage = "Invalid login credentials" };

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.SendAsync(BuildDeleteRequest("the-wrong-password"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        // The important assertion: nothing was touched — deletion never even
        // reached IAccountDeletionService, let alone Supabase.
        Assert.That(_fakeAuthClient.DeleteUserCalledWith, Is.Null);

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var remainingUser = await assertDbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == user.Id);
        Assert.That(remainingUser, Is.Not.Null);
    }

    [Test]
    public async Task REQ710_DeleteAccount_SupabaseDeleteFails_Returns500AndLocalDataIsAlreadyGone()
    {
        var authProviderUserId = Guid.NewGuid();
        var user = await SeedDeletableUserAsync(authProviderUserId);
        _fakeAuthClient.DeleteUserResult = _ => false;

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.SendAsync(BuildDeleteRequest("the-correct-password"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        // Documented, deliberate ordering (AccountDeletionService's own doc
        // comment) — the local writes already committed before the Supabase
        // call ran, so the local User row is gone even though the overall
        // request reports failure.
        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var remainingUser = await assertDbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == user.Id);
        Assert.That(remainingUser, Is.Null);
    }

    [Test]
    public async Task REQ710_DeleteAccount_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(BuildDeleteRequest("irrelevant-password"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(_fakeAuthClient.DeleteUserCalledWith, Is.Null);
    }

    [Test]
    public async Task REQ710_DeleteAccount_AnonymizesCallersGuessesButNotOtherUsersGuesses()
    {
        var deletedUserAuthProviderId = Guid.NewGuid();
        var deletedUser = await SeedDeletableUserAsync(deletedUserAuthProviderId, email: "deleted-user@example.com");
        var otherUser = await SeedDeletableUserAsync(Guid.NewGuid(), email: "other-user@example.com");
        await SeedGuessAsync(deletedUser.Id);
        await SeedGuessAsync(otherUser.Id);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(deletedUserAuthProviderId));

        var response = await client.SendAsync(BuildDeleteRequest("the-correct-password"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var guesses = await assertDbContext.Guesses.AsNoTracking().ToListAsync();
        Assert.That(guesses, Has.Count.EqualTo(2));
        // The other user's guess is untouched — still linked to them, not
        // anonymized as a side effect of someone else's deletion.
        Assert.That(guesses.Count(g => g.UserId == otherUser.Id), Is.EqualTo(1));
        Assert.That(guesses.Count(g => g.UserId == null), Is.EqualTo(1));
        Assert.That(guesses.Count(g => g.UserId == deletedUser.Id), Is.EqualTo(0));
    }

    // REQ-717/ADR-0036: a guest has no password to re-confirm at all — this
    // self-service deletion flow (built around re-proving a password) simply
    // doesn't apply to that identity kind (AuthController.DeleteAccount's own
    // comment). A guest can still be removed via S-026's admin-triggered
    // path instead (AdminManagementEndpointTests' REQ-506 coverage), which
    // doesn't go through this re-confirmation step at all.
    [Test]
    public async Task REQ717_DeleteAccount_Delete_ReturnsBadRequest_ForGuestAccountWithNoPasswordToConfirm()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedGuestUserAsync(authProviderUserId);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.SendAsync(BuildDeleteRequest("irrelevant-password"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        // The important assertion: this never even attempts to re-verify a
        // password a guest doesn't have — the check is rejected before any
        // call to Supabase, same discipline as every other pre-check in this
        // controller.
        Assert.That(_fakeAuthClient.DeleteUserCalledWith, Is.Null);

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var stillThere = await assertDbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.IsGuest);
        Assert.That(stillThere, Is.Not.Null, "the guest row must be untouched by the rejected request");
    }

    private static HttpRequestMessage BuildDeleteRequest(string password) =>
        new(HttpMethod.Delete, "/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccountRequest(password)),
        };

    // Minimal shape for reading a ProblemDetails-style JSON body back in
    // tests (REQ-701's enumeration-safe error, REQ-606's 429 body) — explicit
    // [JsonPropertyName] rather than relying on default (de)serialization
    // case-matching, same idiom SupabaseAuthClient's own SupabaseErrorResponse
    // already uses for parsing an external JSON shape.
    private record ProblemDetailsBody
    {
        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("detail")]
        public string? Detail { get; init; }
    }

    // Test double for ISupabaseAuthClient (COMP-01's only path to the auth
    // provider — see ADR-0013): never makes a real HTTP call, and exposes
    // SignUpCalled so REQ701's checkbox test can assert Supabase is never
    // contacted when the checkbox is unchecked.
    private class FakeSupabaseAuthClient : ISupabaseAuthClient
    {
        public bool SignUpCalled { get; private set; }

        public Func<string, string, SupabaseAuthResult> SignUpResult { get; set; } =
            (_, _) => new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid() };

        public Func<string, string, SupabaseAuthResult> SignInResult { get; set; } =
            (_, _) => new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid(), AccessToken = "fake-access-token" };

        public Task<SupabaseAuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            SignUpCalled = true;
            return Task.FromResult(SignUpResult(email, password));
        }

        public Task<SupabaseAuthResult> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(SignInResult(email, password));

        // REQ-715: controllable per-test, same idiom as SignInResult above —
        // default succeeds so most tests don't need to set it up.
        public Func<string, SupabaseAuthResult> RefreshResult { get; set; } =
            token => new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid(), AccessToken = "a-refreshed-access-token", RefreshToken = "a-refreshed-refresh-token" };

        public string? RefreshTokenCalledWith { get; private set; }

        public Task<SupabaseAuthResult> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            RefreshTokenCalledWith = refreshToken;
            return Task.FromResult(RefreshResult(refreshToken));
        }

        public Guid? DeleteUserCalledWith { get; private set; }

        public Func<Guid, bool> DeleteUserResult { get; set; } = _ => true;

        public Task<bool> DeleteUserAsync(Guid authProviderUserId, CancellationToken cancellationToken = default)
        {
            DeleteUserCalledWith = authProviderUserId;
            return Task.FromResult(DeleteUserResult(authProviderUserId));
        }

        // REQ-717: controllable per-test, same idiom as SignUpResult/
        // SignInResult above — default succeeds so most tests don't need to
        // set it up.
        public bool SignInAnonymouslyCalled { get; private set; }

        // REQ-717's 2026-07-21 "Bot-check (captcha)" addition: captures the
        // token AuthController.Guest forwarded, so a test can assert it was
        // passed through unmodified.
        public string? SignInAnonymouslyCalledWithCaptchaToken { get; private set; }

        public Func<string, SupabaseAuthResult> SignInAnonymouslyResult { get; set; } =
            _ => new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid(), AccessToken = "a-fake-guest-access-token", RefreshToken = "a-fake-guest-refresh-token" };

        public Task<SupabaseAuthResult> SignInAnonymouslyAsync(string captchaToken, CancellationToken cancellationToken = default)
        {
            SignInAnonymouslyCalled = true;
            SignInAnonymouslyCalledWithCaptchaToken = captchaToken;
            return Task.FromResult(SignInAnonymouslyResult(captchaToken));
        }

        public string? LinkEmailPasswordCalledWithAccessToken { get; private set; }
        public string? LinkEmailPasswordCalledWithEmail { get; private set; }

        public Func<string, string, string, SupabaseAuthResult> LinkEmailPasswordResult { get; set; } =
            (_, _, _) => new SupabaseAuthResult { Success = true, AuthProviderUserId = Guid.NewGuid() };

        public Task<SupabaseAuthResult> LinkEmailPasswordAsync(string accessToken, string email, string password, CancellationToken cancellationToken = default)
        {
            LinkEmailPasswordCalledWithAccessToken = accessToken;
            LinkEmailPasswordCalledWithEmail = email;
            return Task.FromResult(LinkEmailPasswordResult(accessToken, email, password));
        }
    }
}
