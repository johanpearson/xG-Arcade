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
    ILeagueRepository leagueRepository) : ControllerBase
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

        var signUpResult = await authClient.SignUpAsync(request.Email, request.Password, cancellationToken);
        if (!signUpResult.Success)
        {
            return Problem(
                title: "Signup failed",
                detail: signUpResult.ErrorMessage,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var user = await userRepository.AddAsync(new User
        {
            Id = Guid.NewGuid(),
            // Safe: SupabaseAuthClient.PostCredentialsAsync never returns
            // Success = true without AuthProviderUserId set.
            AuthProviderUserId = signUpResult.AuthProviderUserId!.Value,
            Email = request.Email,
            DisplayName = displayName,
            EmailConfirmed = true, // Tier 0: Supabase's confirm-email requirement is off — see MVP-SCOPE.md
            CreatedAt = DateTime.UtcNow,
        }, cancellationToken);

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
        // set (see SupabaseAuthClient.PostCredentialsAsync) — unlike Signup,
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

        return Ok(new MeResponse(user.Id, user.Email, user.DisplayName, user.EmailConfirmed));
    }
}
