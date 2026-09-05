using Microsoft.AspNetCore.RateLimiting;
using XGArcade.Api.Admin;
using XGArcade.Api.Announcements;
using XGArcade.Api.Auth;
using XGArcade.Api.Avatars;
using XGArcade.Api.Connect;
using XGArcade.Api.Grid;
using XGArcade.Api.Guesses;
using XGArcade.Api.Incidents;
using XGArcade.Api.Leagues;
using XGArcade.Api.Notifications;
using XGArcade.Api.Path;
using XGArcade.Api.Players;
using XGArcade.Api.Predict;
using XGArcade.Api.Rounds;
using XGArcade.Api.Social;
using XGArcade.Api.Suggestions;
using XGArcade.Api.Users;

namespace XGArcade.Api.CompositionRoot;

// The HTTP request pipeline (middleware order) and every Minimal-API
// endpoint-mapping call. Extracted out of Program.cs (S-102) as a pure
// reorganization, no behavior change.
public static class EndpointMapping
{
    public static void ConfigurePipeline(this WebApplication app)
    {
        app.UseHttpsRedirection();

        app.UseCors("Frontend");

        // REQ-606: before authentication, so an unauthenticated brute-force burst
        // against /auth/signup or /auth/login is rejected as cheaply as possible —
        // matches the recommended ordering for Microsoft.AspNetCore.RateLimiting
        // (after routing/CORS, no requirement to run after authentication since the
        // two endpoints it applies to are both anonymous anyway).
        app.UseRateLimiter();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapControllers();

        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

        app.MapInternalGridEndpoints();
        app.MapInternalRoundEndpoints();
        // REQ-718/ADR-0038: the scheduled-purge half of guest account cleanup, same
        // bearer-token /internal/* pattern as MapInternalRoundEndpoints above.
        app.MapInternalGuestCleanupEndpoints();
        // REQ-1305/ADR-0097: xG Predict's own asynchronous per-match grading
        // job endpoint, same bearer-token /internal/* pattern as
        // MapInternalRoundEndpoints above — its only caller is
        // .github/workflows/grade-predict-matches.yml's hourly cron.
        app.MapInternalPredictGradingEndpoints();
        // REQ-1403/ADR-0103 (S-210): the matchmaking pairing/expiry sweep —
        // same bearer-token /internal/* pattern as MapInternalRoundEndpoints
        // above; see InternalMatchmakingSweepEndpoints.cs's own doc comment
        // for why this uses that pattern rather than
        // sweep-recent-transfers.yml's CLI-verb one (ADR-0024).
        app.MapInternalMatchmakingSweepEndpoints();
        // REQ-1405/ADR-0103 (S-212): the xG Connect per-player forfeit-
        // timeout/resolution sweep — same bearer-token /internal/* pattern
        // as MapInternalMatchmakingSweepEndpoints above; see
        // InternalConnectForfeitSweepEndpoints.cs's own doc comment for why
        // this uses that pattern rather than the CLI-verb one (ADR-0024).
        app.MapInternalConnectForfeitSweepEndpoints();
        // S-218's own E2E accept criterion: deterministic
        // PlayerCareerStint-backed test data for REQ-1404's target-pick
        // overlap check and REQ-1406's chain-step overlap check, same
        // environment-gated /internal/test-data/* pattern as
        // MapInternalRoundEndpoints's own seed-guessable-*-round endpoints
        // (the gate lives inside this method itself, not here — see that
        // file's own top-of-file comment for the full "why," including a
        // flagged, test-only id-space workaround).
        app.MapInternalConnectTestDataEndpoints();
        app.MapRoundEndpoints();
        // REQ-1203/S-082: xg-path's own read-only display endpoint (GET
        // /path/current) — POST /rounds/{roundId}/cells/{cellId}/guesses below
        // remains the only write path for both games (already game-agnostic, see
        // PathEndpoints.cs's own doc comment).
        app.MapPathEndpoints();
        app.MapGuessEndpoints();
        // REQ-1302/1303/1306: xG Predict's own read/write surface (GET
        // /predict/current, POST /predict/matches/{matchId}/predictions,
        // POST /predict/confirm) — deliberately NOT routed through
        // MapGuessEndpoints above (ADR-0096: predictions are structurally
        // incompatible with Guess/IGuessSubmissionService). See
        // PredictEndpoints.cs's own doc comment.
        app.MapPredictEndpoints();
        // REQ-215 (S-089): the submission-only half — REQ-509/510's admin review/
        // commit/reject half is MapAdminSuggestionEndpoints below.
        app.MapSuggestionEndpoints();
        app.MapLeaderboardEndpoints();
        app.MapLeagueEndpoints();
        // REQ-411/S-178: GET /users/{userId}/stats — read-only stats/profile
        // view, own and any other player's, reusing LeaderboardService.
        app.MapUserEndpoints();
        app.MapAdminEndpoints();
        // REQ-509/REQ-510 (S-090): suggestion review/commit/reject + the standalone
        // manual search-and-add path — its own file/registration, never folded into
        // MapAdminEndpoints above (ADR-0053, see AdminSuggestionEndpoints.cs's own
        // doc comment).
        app.MapAdminSuggestionEndpoints();
        // REQ-1414/ADR-0053: read-only admin list of xG Connect dispute
        // data-correction suggestions — its own file/registration, same
        // "submission file vs. admin file"-style split as
        // MapAdminSuggestionEndpoints above.
        app.MapAdminConnectDisputeSuggestionEndpoints();
        // REQ-507/508: guest/user metrics + bulk guest force-clear, registered
        // unconditionally (including Production) — see that file's own doc comment
        // for why these are kept separate from MapAdminManagementEndpoints below.
        app.MapAdminAccountsEndpoints();
        // S-026: REQ-505/506, non-Production only — see that file's own doc comment
        // for why these are kept separate from MapAdminEndpoints above.
        app.MapAdminManagementEndpoints();
        // REQ-1209/ADR-0058: xG Path's cycle-state admin read, registered
        // unconditionally (including Production) — see that file's own doc comment
        // for why it's kept separate from MapAdminManagementEndpoints above.
        app.MapAdminXGPathEndpoints();
        app.MapPlayerAutocompleteEndpoints();
        // REQ-903/ADR-0064/COMP-12: in-app bug reports -> GitHub issues in this
        // repo, non-guest only (enforced server-side inside the handler itself).
        app.MapIncidentEndpoints();
        // REQ-904/ADR-0066: admin-only read of the cached open-incident-issue
        // count — its own file/registration, same "submission file vs. admin
        // file" split as MapSuggestionEndpoints/MapAdminSuggestionEndpoints above,
        // never folded into MapAdminEndpoints.
        app.MapAdminIncidentReportEndpoints();
        // REQ-511: the public, unauthenticated read path (GET /announcement-banner)
        // — see AnnouncementBannerEndpoints.cs's own doc comment for why this is
        // registered unconditionally, with no .RequireAuthorization() anywhere in
        // it, same as GET /health above.
        app.MapAnnouncementBannerEndpoints();
        // REQ-511: admin create/edit/activate/deactivate — its own file/
        // registration, never folded into MapAdminEndpoints above, same
        // "submission file vs. admin file" split as MapSuggestionEndpoints/
        // MapAdminSuggestionEndpoints.
        app.MapAdminAnnouncementBannerEndpoints();
        // REQ-722/ADR-0087 (S-180): POST /users/me/avatar — a logged-in
        // player's avatar upload, pending admin approval (REQ-517/S-181,
        // not this endpoint). Its own file/registration, same
        // one-file-per-feature convention as every other Map*Endpoints call
        // above.
        app.MapAvatarEndpoints();
        // REQ-517 (S-181): admin review (list/approve/reject) of the
        // Pending queue MapAvatarEndpoints above feeds — its own file/
        // registration, same "submission file vs. admin file" split as
        // MapSuggestionEndpoints/MapAdminSuggestionEndpoints above.
        app.MapAdminAvatarEndpoints();
        // REQ-1401/S-209/ADR-0103: Core.Social (COMP-16)'s friend request
        // send/accept/decline surface — its own file/registration, no
        // dependency on any Games.XGConnect (COMP-17) code (that's
        // S-210+'s match/challenge logic).
        app.MapFriendEndpoints();
        // REQ-1402/S-210/ADR-0103: Core.Social (COMP-16)'s direct-challenge
        // send/accept/decline surface — its own file/registration, same
        // "submission file vs. admin file"-style per-feature split as
        // MapFriendEndpoints above. The accept handler is the one place
        // this story writes a Games.XGConnect ConnectMatch row, per
        // ADR-0103's orchestration-lives-in-XGArcade.Api requirement.
        app.MapChallengeEndpoints();
        // REQ-1403/S-210/ADR-0103: Core.Social's random-matchmaking opt-in
        // surface — pairing itself happens only via the scheduled sweep
        // (MapInternalMatchmakingSweepEndpoints above), never from this
        // player-triggered endpoint.
        app.MapMatchmakingEndpoints();
        // REQ-1404/S-211/ADR-0103: Games.XGConnect (COMP-17)'s own
        // target-pick selection surface — its own file/registration, same
        // per-feature split as MapFriendEndpoints/MapChallengeEndpoints
        // above. Unlike those two, this lives in Games.XGConnect's own
        // service (IConnectTargetPickService), not Core.Social, since
        // target-pick selection is entirely COMP-17-internal state
        // (ConnectTargetPick), never orchestrated against Core.Social.
        app.MapConnectMatchEndpoints();
        // REQ-1406/S-213/ADR-0103: Games.XGConnect (COMP-17)'s own
        // chain-step submission surface — its own file/registration, same
        // per-feature split as MapConnectMatchEndpoints immediately above.
        app.MapConnectChainStepEndpoints();
        // REQ-1410/S-215/ADR-0103: Games.XGConnect (COMP-17)'s own in-match
        // chat send/read surface — its own file/registration, same per-
        // feature split as MapConnectChainStepEndpoints immediately above.
        app.MapConnectChatEndpoints();
        // REQ-1404/1405/1406/1409/1411/ADR-0103, S-218 prep: GET /matches +
        // GET /matches/{matchId} — the read-only surface unblocking S-218's
        // frontend gameplay screen (every xG Connect endpoint above this
        // one is write-only). Its own file/registration, same per-feature
        // split as MapConnectMatchEndpoints/MapConnectChainStepEndpoints/
        // MapConnectChatEndpoints above.
        app.MapConnectMatchQueryEndpoints();
        // REQ-1412/1413/ADR-0103/ADR-0109: Games.XGConnect (COMP-17)'s own
        // dispute-a-failed-chain-step raise/approve/deny/list surface — its
        // own file/registration, same per-feature split as every other
        // Map*Endpoints call in this section.
        app.MapConnectChainStepDisputeEndpoints();
        // REQ-1411/S-216/ADR-0103: the visible-notification-indicator
        // aggregate read (GET /notifications/summary) — combines pending
        // friend requests (Core.Social/COMP-16), pending challenges
        // (Core.Social/COMP-16), and open xG Connect matches still awaiting
        // the caller's own next move (Games.XGConnect/COMP-17). Not a new
        // component per ADR-0103's own "belongs to neither" paragraph — see
        // NotificationEndpoints.cs's own doc comment.
        app.MapNotificationEndpoints();
    }
}
