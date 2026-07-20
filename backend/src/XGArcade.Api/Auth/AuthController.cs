using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    [HttpPost("signup")]
    public async Task<IActionResult> Signup([FromBody] SignupRequest request, CancellationToken cancellationToken)
    {
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
            return Problem(
                title: "Signup failed",
                detail: signUpResult.ErrorMessage,
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

        return CreatedAtAction(nameof(Me), null, new SignupResponse(user.Id, user.Email, user.DisplayName));
    }

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

        return Ok(new MeResponse(user.Id, user.Email, user.DisplayName, user.EmailConfirmed, isAdmin));
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
