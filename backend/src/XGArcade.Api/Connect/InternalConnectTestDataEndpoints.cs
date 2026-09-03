using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Connect;

// REQ-1404/1406, S-218's own E2E accept criterion ("a full match happy
// path... challenge -> both picks -> chain to completion -> resolution"):
// deterministic, hermetic test data for xG Connect's target-pick and
// chain-step flows, the same "bypass live Wikidata, write via each
// component's own repository" discipline
// XGArcade.Api.Rounds.InternalRoundEndpoints's three seed-guessable-*
// endpoints already establish (ADR-0006 boundary rule 4 — never a raw
// table write). REQ-1404/1406 both resolve real-vs-fake connectivity via
// IPlayerCareerOverlapService, which trusts already-persisted
// PlayerCareerStint rows once at least one exists per player and only ever
// falls back to a live Wikidata refresh when a player has ZERO cached
// stints (see that service's own doc comment) — seeding stints directly
// here means neither REQ-1404's target-pick overlap check nor REQ-1406's
// chain-step overlap check ever reaches Wikidata during an E2E run.
//
// **Known, deliberately flagged workaround — not a design choice.**
// TargetPickPanel.tsx (design-document.md SCREEN-16) requires selecting a
// suggestion from `/players/autocomplete` (COMP-10, PlayerNameIndex) before
// "Set target pick" is even enabled, and submits that suggestion's own
// `playerId` as `POST /matches/{matchId}/target-pick`'s `targetPlayerId`.
// But `ConnectTargetPickService`/`PlayerCareerOverlapService` resolve that
// id against `PlayerCareerStint`/`Player` (COMP-06) — a DIFFERENT id space
// from PlayerNameIndex.PlayerId, which is a synthetic, QID-derived hash
// with, per that entity's own doc comment, "no guaranteed relationship to
// any separately-created Player.Id... for the same real person" (ADR-0007).
// For real, Wikidata-imported players these two ids will practically always
// differ, so submitting a target pick chosen via the real autocomplete UI
// against real data does not reliably resolve to the intended player's own
// PlayerCareerStint rows today. This endpoint seeds a matching
// PlayerNameIndex row with PlayerId set equal to the real Player.Id purely
// so this test-only pair of players can be found by name and selected
// through the actual UI control — this does NOT fix, and must not be read
// as evidence there is nothing to fix in, that underlying id-space mismatch
// for real production data. Flagged back to the team (see this story's own
// handoff notes) for architecture-reviewer/backend-implementer/
// ui-implementer to resolve properly — e.g. by having the target-pick
// endpoint resolve a player by name server-side the same way
// ConnectChainStepService.SubmitChainStepAsync already does via
// IPlayerRepository.GetPlayersByNormalizedFullNameAsync, rather than
// trusting a client-supplied id from a different component's id space.
//
// The chain-step candidate field (ChainBuilder.tsx) has no such issue — it
// submits typed name text, resolved server-side via that same
// GetPlayersByNormalizedFullNameAsync call — so the connector player seeded
// below gets no PlayerNameIndex row at all; only the two target-pick
// players need one.
public static class InternalConnectTestDataEndpoints
{
    public static void MapInternalConnectTestDataEndpoints(this WebApplication app)
    {
        // REQ-806/ADR-0006: absent entirely when ASPNETCORE_ENVIRONMENT ==
        // Production, checked before the route is registered — same
        // discipline InternalRoundEndpoints.cs's own test-data endpoints
        // use immediately below their own production early-return.
        if (app.Environment.IsProduction())
            return;

        app.MapPost("/internal/test-data/seed-connect-players", async (
            IPlayerRepository playerRepository,
            IPlayerCareerStintRepository playerCareerStintRepository,
            IPlayerNameIndexRepository playerNameIndexRepository,
            CancellationToken cancellationToken) =>
        {
            // Same unique-tag-per-call convention as
            // InternalRoundEndpoints.CreateUniqueTestPlayerAsync (REQ-209
            // fallout) — keeps repeated/concurrent E2E runs hermetic against
            // a shared CI Postgres instance.
            var tag = Guid.NewGuid().ToString("N")[..8];

            var targetA = await playerRepository.AddPlayerAsync(
                new Player { Id = Guid.NewGuid(), FullName = $"Connect Target Alpha {tag}", WikidataQid = $"Qtest-{Guid.NewGuid()}" },
                cancellationToken);
            var targetB = await playerRepository.AddPlayerAsync(
                new Player { Id = Guid.NewGuid(), FullName = $"Connect Target Beta {tag}", WikidataQid = $"Qtest-{Guid.NewGuid()}" },
                cancellationToken);
            var connector = await playerRepository.AddPlayerAsync(
                new Player { Id = Guid.NewGuid(), FullName = $"Connect Connector Charlie {tag}", WikidataQid = $"Qtest-{Guid.NewGuid()}" },
                cancellationToken);

            var clubOverlappingWithA = $"Seed Overlap Club A {tag}";
            var clubOverlappingWithB = $"Seed Overlap Club B {tag}";

            // Target A and Target B share no club at all -> REQ-1404's
            // trivially-connected check must pass (not connected) so the
            // match can actually start.
            await playerCareerStintRepository.AddCareerStintsAsync(
                targetA.Id,
                [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = targetA.Id, ClubName = clubOverlappingWithA, StartYear = 2010, EndYear = 2012, SequenceOrder = 0 }],
                cancellationToken);
            await playerCareerStintRepository.AddCareerStintsAsync(
                targetB.Id,
                [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = targetB.Id, ClubName = clubOverlappingWithB, StartYear = 2015, EndYear = 2017, SequenceOrder = 0 }],
                cancellationToken);

            // The connector genuinely overlaps BOTH targets, at two
            // different clubs/periods of its own — closes either target's
            // one-step chain symmetrically, regardless of which target a
            // given match participant started from (REQ-1406/1408's
            // "exactly one connector, zero penalties" minimum-score case).
            await playerCareerStintRepository.AddCareerStintsAsync(
                connector.Id,
                [
                    new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = connector.Id, ClubName = clubOverlappingWithA, StartYear = 2010, EndYear = 2012, SequenceOrder = 0 },
                    new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = connector.Id, ClubName = clubOverlappingWithB, StartYear = 2015, EndYear = 2017, SequenceOrder = 1 },
                ],
                cancellationToken);

            // TEST-ONLY id-space alignment — see this file's own top-of-file
            // comment for exactly why this exists and why it is not a real
            // fix for the underlying mismatch.
            await playerNameIndexRepository.UpsertManyAsync(
                [
                    new PlayerNameIndex
                    {
                        PlayerId = targetA.Id,
                        PrimaryName = targetA.FullName,
                        NormalizedName = PlayerNameNormalizer.Normalize(targetA.FullName),
                    },
                    new PlayerNameIndex
                    {
                        PlayerId = targetB.Id,
                        PrimaryName = targetB.FullName,
                        NormalizedName = PlayerNameNormalizer.Normalize(targetB.FullName),
                    },
                ],
                cancellationToken);

            return Results.Ok(new SeedConnectPlayersResponse(
                targetA.FullName, targetB.FullName, connector.FullName, clubOverlappingWithA, clubOverlappingWithB));
        });
    }
}

// TargetPlayerAName/TargetPlayerBName: searchable (via PlayerNameIndex,
// see this file's own top-of-file comment) and selectable through
// TargetPickPanel.tsx's real UI. ConnectorPlayerName: typed directly into
// ChainBuilder.tsx's candidate field (resolved server-side by exact
// normalized name, no PlayerNameIndex/autocomplete-suggestion selection
// needed — see ConnectChainStepService.SubmitChainStepAsync). Club*: the
// exact "Claimed shared club" text a caller must submit for a step starting
// from that target to validate.
public record SeedConnectPlayersResponse(
    string TargetPlayerAName,
    string TargetPlayerBName,
    string ConnectorPlayerName,
    string ClubOverlappingWithA,
    string ClubOverlappingWithB);
