using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using XGArcade.Core.Auth;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Auth;

// COMP-01 (Core.Users): signup/login mediated through Supabase Auth
// (ADR-0013). Only REQ-701's checkbox clause is enforced here — REQ-702
// through REQ-705 (confirmation flow) are deferred per MVP-SCOPE.md; Tier 0
// runs with Supabase's "confirm email" requirement turned off.
[ApiController]
[Route("auth")]
public class AuthController(
    ISupabaseAuthClient authClient,
    IUserRepository userRepository,
    ILeagueRepository leagueRepository,
    IAccountDeletionService accountDeletionService,
    IConfiguration configuration,
    ILogger<AuthController> logger) : ControllerBase
{
    // REQ-606: 10 signups per IP per minute — see Program.cs's
    // AddRateLimiter registration for the shared policy/OnRejected details.
    [EnableRateLimiting("auth-signup")]
    [HttpPost("signup")]
    public async Task<IActionResult> Signup([FromBody] SignupRequest request, CancellationToken cancellationToken)
    {
        // REQ-701 password policy (docs/requirements-document.md §5's
        // "sensible technical default": minimum 8 characters, no forced
        // complexity rules, following NIST 800-63B) — checked first among
        // the free, local-only checks (no DB round trip, no Supabase call)
        // since a password failing this makes the confirm-password match
        // below moot. Matches AuthScreen.tsx's client-side check order.
        if (request.Password.Length < 8)
        {
            return Problem(
                title: "Password too short",
                detail: "Password must be at least 8 characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // REQ-701: confirm-password must match before Supabase Auth is ever
        // called — same "checked before any call to Supabase" discipline as
        // the age checkbox and DisplayName checks below. Checked first,
        // matching AuthScreen.tsx's client-side check order.
        if (request.Password != request.ConfirmPassword)
        {
            return Problem(
                title: "Passwords do not match",
                detail: "Password and confirm password must match.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // REQ-401/404: DisplayName is what a leaderboard shows another
        // player instead of their email — required, same "checked before
        // any call to Supabase" discipline as the confirm-password check above.
        var displayName = request.DisplayName.Trim();
        if (string.IsNullOrEmpty(displayName) || displayName.Length > 30)
        {
            return Problem(
                title: "Display name required",
                detail: "Display name must be between 1 and 30 characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // REQ-701: signup cannot proceed without the checkbox — checked
        // before any call to Supabase, so an unchecked box never creates an
        // identity anywhere.
        if (!request.AgeConfirmed)
        {
            return Problem(
                title: "Age confirmation required",
                detail: "You must confirm you are at least 16 years old to create an account.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // REQ-701: uniqueness is case-insensitive only — spaces and
        // formatting stay exactly as entered, no username-style reshaping.
        // Checked before Supabase Auth is ever called, same discipline as
        // the checks above (ordered last among the free, local-only checks
        // since it's the only one that costs a DB round trip); the DB's own
        // unique index (UserRepository.AddAsync) is the race-safety net
        // behind this check, not the primary mechanism.
        if (await userRepository.DisplayNameExistsAsync(displayName, cancellationToken: cancellationToken))
        {
            return DisplayNameConflictProblem();
        }

        var signUpResult = await authClient.SignUpAsync(request.Email, request.Password, cancellationToken);
        if (!signUpResult.Success)
        {
            // REQ-701 account-enumeration-safe error: Supabase's own
            // rejection reason (e.g. "User already registered") is
            // deliberately never passed through to the client, and — unlike
            // the DisplayName conflict above — this is not narrowed to only
            // the already-registered case. Giving a distinct, specific
            // message just for "already registered" (while passing every
            // other Supabase rejection's own text through unchanged) would
            // itself leak which case occurred, exactly the enumeration this
            // exists to prevent. So every Supabase signup rejection gets
            // this same generic detail, worded to read sensibly whether or
            // not an account already exists for this address — logged
            // server-side (full reason, per docs/coding-guidelines.md) so a
            // genuine misconfiguration is still diagnosable from logs.
            logger.LogWarning("Signup rejected by Supabase Auth: {ErrorMessage}", signUpResult.ErrorMessage);
            return Problem(
                title: "Signup could not be completed",
                detail: "Check your email to confirm your account, or reset your password if you already have one.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        User user;
        try
        {
            user = await userRepository.AddAsync(new User
            {
                Id = Guid.NewGuid(),
                // Safe: SupabaseAuthClient.PostAuthRequestAsync never returns
                // Success = true without AuthProviderUserId set.
                AuthProviderUserId = signUpResult.AuthProviderUserId!.Value,
                Email = request.Email,
                DisplayName = displayName,
                EmailConfirmed = true, // Tier 0: Supabase's confirm-email requirement is off — see MVP-SCOPE.md
                CreatedAt = DateTime.UtcNow,
            }, cancellationToken);
        }
        catch (DisplayNameAlreadyInUseException ex)
        {
            // The check above raced with another signup for the same
            // display name and lost — same clear error either way, never a
            // raw 500 from the DB's constraint violation. Logged (coding-
            // guidelines.md: "log the full exception server-side") since
            // this is otherwise invisible — DisplayNameExistsAsync's own
            // pre-check already returned false moments earlier.
            logger.LogWarning(ex, "Signup lost a race on display name uniqueness.");
            return DisplayNameConflictProblem();
        }

        // REQ-401: "requires no action from the user" — done automatically
        // here, right after the local User row exists, never left to a
        // separate step the player (or a caller of this endpoint) could skip.
        var globalLeague = await leagueRepository.GetOrCreateGlobalLeagueAsync(cancellationToken);
        await leagueRepository.AddMembershipAsync(globalLeague.Id, user.Id, cancellationToken);

        // Safe: this signup path always sets Email above — the null-forgiving
        // operator here is only about User.Email's REQ-717 nullability (a
        // guest has none), never true for a row created through this path.
        return CreatedAtAction(nameof(Me), null, new SignupResponse(user.Id, user.Email!, user.DisplayName));
    }

    // REQ-606: 10 logins per IP per minute — a separate policy/counter from
    // signup's above, so exhausting one never blocks the other. See
    // Program.cs's AddRateLimiter registration for the shared policy/
    // OnRejected details.
    [EnableRateLimiting("auth-login")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var signInResult = await authClient.SignInWithPasswordAsync(request.Email, request.Password, cancellationToken);

        // ISupabaseAuthClient.Success only guarantees AuthProviderUserId is
        // set (see SupabaseAuthClient.PostAuthRequestAsync) — unlike Signup,
        // Login's response is useless to the caller without a token, so
        // that's checked explicitly here rather than force-unwrapped.
        if (!signInResult.Success || signInResult.AccessToken is null)
        {
            return Problem(
                title: "Login failed",
                detail: signInResult.ErrorMessage,
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return Ok(new LoginResponse(signInResult.AccessToken, signInResult.RefreshToken));
    }

    // REQ-717/ADR-0036: provisions a real, guessable User row with no email
    // or password — the identity itself is a Supabase Auth Anonymous
    // Sign-in, mediated here the same way Signup/Login are (ADR-0013),
    // never called directly from the frontend. Rate-limited by its own,
    // tighter policy (see Program.cs's "auth-guest" — an anonymous sign-in
    // has even less friction than email signup, no address to type at all,
    // making it a cheaper target for scripted identity creation). No
    // request body: there's nothing for the caller to supply — REQ-717's
    // whole point is zero-friction entry.
    [EnableRateLimiting("auth-guest")]
    [HttpPost("guest")]
    public async Task<IActionResult> Guest(CancellationToken cancellationToken)
    {
        var signInResult = await authClient.SignInAnonymouslyAsync(cancellationToken);
        if (!signInResult.Success || signInResult.AccessToken is null)
        {
            logger.LogWarning("Guest sign-in rejected by Supabase Auth: {ErrorMessage}", signInResult.ErrorMessage);
            return Problem(
                title: "Guest sign-in failed",
                detail: "Could not start a guest session. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var displayName = await GenerateUniqueGuestDisplayNameAsync(cancellationToken);

        User user;
        try
        {
            user = await userRepository.AddAsync(new User
            {
                Id = Guid.NewGuid(),
                // Safe: SupabaseAuthClient.PostAuthRequestAsync never returns
                // Success = true without AuthProviderUserId set.
                AuthProviderUserId = signInResult.AuthProviderUserId!.Value,
                Email = null,
                DisplayName = displayName,
                // No email exists to be confirmed at all — distinct from
                // Signup's `true` (Tier 0's confirm-email-off default for a
                // *real* signup). REQ-702's "unconfirmed accounts cannot
                // play" rule doesn't gate on this today either way (deferred
                // per MVP-SCOPE.md) — this is a factual "nothing to confirm"
                // rather than one that matters operationally yet.
                EmailConfirmed = false,
                IsGuest = true,
                CreatedAt = DateTime.UtcNow,
            }, cancellationToken);
        }
        catch (DisplayNameAlreadyInUseException ex)
        {
            // Astronomically unlikely — GenerateUniqueGuestDisplayNameAsync
            // already retried against the same pre-check — but the same
            // DB-level race fallback every other write against this unique
            // index uses (Signup/UpdateDisplayName above).
            logger.LogWarning(ex, "Guest sign-in lost a race on display name uniqueness.");
            return Problem(
                title: "Guest sign-in failed",
                detail: "Could not start a guest session. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // REQ-401/REQ-717: a guest is a real User row participating in the
        // Global league exactly like any other new account — same
        // auto-enrollment Signup already does, no second, guest-specific
        // mechanism.
        var globalLeague = await leagueRepository.GetOrCreateGlobalLeagueAsync(cancellationToken);
        await leagueRepository.AddMembershipAsync(globalLeague.Id, user.Id, cancellationToken);

        // Same token shape as Login/Refresh — the frontend treats a guest
        // session identically to any other login for storage purposes
        // (ADR-0033), with no separate "guest mode" client-side branch.
        return Ok(new LoginResponse(signInResult.AccessToken, signInResult.RefreshToken));
    }

    // REQ-717: Guest####-style default display name, retried on a
    // collision the same way any other conflicting write in this system is
    // retried — reuses REQ-701's existing case-insensitive uniqueness check
    // (IUserRepository.DisplayNameExistsAsync), never a second mechanism.
    private const int MaxGuestDisplayNameGenerationAttempts = 10;

    private async Task<string> GenerateUniqueGuestDisplayNameAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxGuestDisplayNameGenerationAttempts; attempt++)
        {
            var candidate = $"Guest{Random.Shared.Next(1000, 10000)}";
            if (!await userRepository.DisplayNameExistsAsync(candidate, cancellationToken: cancellationToken))
            {
                return candidate;
            }
        }

        // Astronomically unlikely with a 9000-value range at this DB's
        // scale, but never loop forever — falls back to a value with enough
        // entropy to be unique in practice, still well inside REQ-701's
        // 1-30 character bound (5 + 15 = 20 characters).
        return $"Guest{Guid.NewGuid():N}"[..20];
    }

    // REQ-715: exchanges a stored refresh token for a new access token
    // (silently, without the person re-entering credentials) — mediated
    // through Supabase Auth the same way Login/Signup already are
    // (ADR-0013), never a direct frontend-to-Supabase call. Deliberately
    // unauthenticated (no [Authorize]): the caller's whole reason for
    // hitting this endpoint is that they may not have a currently-valid
    // access token at all. Returns the same LoginResponse shape as Login —
    // the frontend treats a successful refresh identically to a fresh login
    // for storage purposes (ADR-0033).
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken cancellationToken)
    {
        var refreshResult = await authClient.RefreshTokenAsync(request.RefreshToken, cancellationToken);

        // REQ-715: an invalid, expired, or revoked refresh token must fail
        // clearly and distinctly (never a generic 500, never left to an
        // unhandled exception) so the frontend's cue to sign the person out
        // to the login screen is unambiguous — this is that cue.
        if (!refreshResult.Success || refreshResult.AccessToken is null)
        {
            return Problem(
                title: "Refresh failed",
                detail: refreshResult.ErrorMessage ?? "Refresh token is invalid, expired, or revoked.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return Ok(new LoginResponse(refreshResult.AccessToken, refreshResult.RefreshToken));
    }

    // The protected endpoint proving the JWT validation middleware works
    // end to end (docs/backlog.md S-004's accept criteria).
    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var authProviderUserId = User.GetAuthProviderUserId();
        if (authProviderUserId is null)
        {
            return Unauthorized();
        }

        var user = await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        // REQ-504: the frontend's only way to know whether to show the admin
        // nav entry point — same Admin:UserIds check the "Admin"
        // authorization policy itself uses (AdminAuthorizationHandler).
        var isAdmin = AdminAuthorizationHandler.IsAdminUserId(configuration, authProviderUserId.Value);

        return Ok(new MeResponse(user.Id, user.Email, user.DisplayName, user.EmailConfirmed, isAdmin, user.IsGuest));
    }

    // REQ-714: edit the caller's own DisplayName from Settings — reuses
    // REQ-701's exact 1-30 character bound and case-insensitive uniqueness
    // mechanism (IUserRepository.DisplayNameExistsAsync/UserRepository
    // .AddAsync's race fallback), not a second one. Checks are ordered the
    // same "free checks before a DB round trip" way Signup already does:
    // length first, uniqueness (the one DB round trip) last.
    [Authorize]
    [HttpPut("display-name")]
    public async Task<IActionResult> UpdateDisplayName([FromBody] UpdateDisplayNameRequest request, CancellationToken cancellationToken)
    {
        var authProviderUserId = User.GetAuthProviderUserId();
        if (authProviderUserId is null)
        {
            return Unauthorized();
        }

        var user = await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        // REQ-714/701: same length bound as Signup, checked before any
        // database write.
        var displayName = request.DisplayName.Trim();
        if (string.IsNullOrEmpty(displayName) || displayName.Length > 30)
        {
            return Problem(
                title: "Display name required",
                detail: "Display name must be between 1 and 30 characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // REQ-714: excludes the caller's own row, so a no-op resubmission of
        // their own current name — including a pure-casing change — is never
        // treated as a conflict against itself.
        if (await userRepository.DisplayNameExistsAsync(displayName, excludeUserId: user.Id, cancellationToken: cancellationToken))
        {
            return DisplayNameConflictProblem();
        }

        try
        {
            var updated = await userRepository.UpdateDisplayNameAsync(user.Id, displayName, cancellationToken);
            if (updated is null)
            {
                return NotFound();
            }

            return Ok(new UpdateDisplayNameResponse(updated.Id, updated.DisplayName));
        }
        catch (DisplayNameAlreadyInUseException ex)
        {
            // The pre-check above raced with another caller's own edit/signup
            // for the same display name and lost — same clear error either
            // way, never a raw 500 from the DB's constraint violation. Same
            // discipline as Signup's identical catch above.
            logger.LogWarning(ex, "Display name update lost a race on uniqueness.");
            return DisplayNameConflictProblem();
        }
    }

    // REQ-717/ADR-0036: the claim/upgrade path — a guest adds a real email
    // and password, converting their existing identity in place (never
    // creating a second, disconnected User row; every Guess/LeagueMembership
    // row already attributed to this User.Id stays attributed to it
    // unchanged, since nothing here touches those tables at all). Rejects
    // outright if the caller isn't currently a guest — an already-real
    // account has no "claim" to perform.
    [Authorize]
    [HttpPost("claim")]
    public async Task<IActionResult> Claim([FromBody] ClaimAccountRequest request, CancellationToken cancellationToken)
    {
        var authProviderUserId = User.GetAuthProviderUserId();
        if (authProviderUserId is null)
        {
            return Unauthorized();
        }

        var user = await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        if (!user.IsGuest)
        {
            return Problem(
                title: "Account is not a guest",
                detail: "Only a guest account can be claimed.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Same REQ-701 password policy/ordering as Signup: free, local-only
        // checks before any call to Supabase.
        if (request.Password.Length < 8)
        {
            return Problem(
                title: "Password too short",
                detail: "Password must be at least 8 characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Password != request.ConfirmPassword)
        {
            return Problem(
                title: "Passwords do not match",
                detail: "Password and confirm password must match.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // The claim/link call must authenticate as the guest identity being
        // converted (see ISupabaseAuthClient.LinkEmailPasswordAsync's own
        // doc comment) — extracted from this request's own bearer token,
        // which [Authorize] already validated to reach this point.
        var accessToken = GetBearerToken();
        if (accessToken is null)
        {
            return Unauthorized();
        }

        var linkResult = await authClient.LinkEmailPasswordAsync(accessToken, request.Email, request.Password, cancellationToken);
        if (!linkResult.Success)
        {
            logger.LogWarning("Claim rejected by Supabase Auth: {ErrorMessage}", linkResult.ErrorMessage);
            return Problem(
                title: "Claim could not be completed",
                detail: "Could not add an email and password to this account. The email may already be in use.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var updated = await userRepository.ClaimGuestAsync(user.Id, request.Email, cancellationToken);
        if (updated is null)
        {
            return NotFound();
        }

        var isAdmin = AdminAuthorizationHandler.IsAdminUserId(configuration, authProviderUserId.Value);
        return Ok(new MeResponse(updated.Id, updated.Email, updated.DisplayName, updated.EmailConfirmed, isAdmin, updated.IsGuest));
    }

    // Shared by Claim above — the raw bearer token this request itself
    // carried in, needed because the claim/link Supabase call must
    // authenticate as the guest identity being converted, not the shared
    // anon key every other ISupabaseAuthClient call here uses. Returns null
    // if the header is somehow missing/malformed, which [Authorize] having
    // already accepted this request should make unreachable in practice —
    // handled defensively rather than assumed.
    private string? GetBearerToken()
    {
        const string bearerPrefix = "Bearer ";
        var authorizationHeader = Request.Headers.Authorization.ToString();
        return authorizationHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader[bearerPrefix.Length..]
            : null;
    }

    // REQ-710: self-service account deletion. Irreversible, so the request
    // must re-prove the caller still holds the account's own credentials —
    // re-verified against Supabase Auth via the same SignInWithPasswordAsync
    // call Login uses, rather than a bare confirmation flag — before any
    // data is touched. The actual anonymize/delete logic lives in
    // IAccountDeletionService, not here, so S-026's admin-triggered deletion
    // (docs/backlog.md) can call the identical path with its own,
    // admin-authorized confirmation instead of a second implementation.
    [Authorize]
    [HttpDelete("account")]
    public async Task<IActionResult> DeleteAccount([FromBody] DeleteAccountRequest request, CancellationToken cancellationToken)
    {
        var authProviderUserId = User.GetAuthProviderUserId();
        if (authProviderUserId is null)
        {
            return Unauthorized();
        }

        var user = await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        // REQ-717: a guest has no email/password to re-confirm at all — this
        // self-service deletion flow, built around re-proving a password,
        // simply doesn't apply to that identity kind. (A guest can still be
        // removed via S-026's admin-triggered path, which doesn't go through
        // this re-confirmation step.)
        if (user.Email is null)
        {
            return Problem(
                title: "Account deletion not available",
                detail: "Guest accounts have no password to confirm deletion.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var confirmation = await authClient.SignInWithPasswordAsync(user.Email, request.Password, cancellationToken);
        if (!confirmation.Success)
        {
            // frontend/src/auth/DeleteAccountScreen.tsx string-matches this exact
            // title to tell "wrong password" (show inline, session still valid)
            // apart from any other 401 on this endpoint (expired/invalid JWT,
            // which has no ProblemDetails body at all and logs the user out
            // instead). If this title ever changes, update that check too —
            // there's no shared machine-readable error code between the two yet.
            return Problem(
                title: "Incorrect password",
                detail: "Account deletion requires your current password to confirm.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var result = await accountDeletionService.DeleteAccountAsync(user.Id, cancellationToken);
        if (!result.Success)
        {
            logger.LogError("Account deletion failed for user {UserId}: {ErrorMessage}", user.Id, result.ErrorMessage);
            return Problem(
                title: "Account deletion failed",
                detail: result.ErrorMessage,
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return NoContent();
    }

    // Shared by both places REQ-701's uniqueness rule can reject a signup —
    // the pre-check and the DB-constraint race fallback — so both give the
    // caller the exact same 409 shape.
    private ObjectResult DisplayNameConflictProblem() =>
        Problem(
            title: "Display name already in use",
            detail: "That display name is already taken. Please choose another.",
            statusCode: StatusCodes.Status409Conflict);
}
