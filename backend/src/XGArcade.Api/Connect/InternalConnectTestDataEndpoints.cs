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
// **Bug found here during this story's Playwright E2E test-writing, now
// fully fixed (backend + frontend).** `PlayerNameIndex.PlayerId` is a
// synthetic, QID-derived hash with, per that entity's own doc comment, "no
// guaranteed relationship to any separately-created Player.Id... for the
// same real person" (ADR-0007) — a DIFFERENT id space from
// `Player.Id`/`PlayerCareerStint.PlayerId` (COMP-06) that
// `ConnectTargetPickService`/`PlayerCareerOverlapService` actually check
// against. `TargetPickPanel.tsx` originally submitted a
// `/players/autocomplete` (COMP-10) suggestion's raw `playerId` straight
// into that mismatched space. Both halves are now fixed the same way
// `ConnectChainStepService.SubmitChainStepAsync`/`ChainBuilder.tsx` already
// handled `candidatePlayerName`: `POST /matches/{matchId}/target-pick` takes
// a player NAME (`SubmitTargetPickRequest.TargetPlayerName`), resolved
// server-side inside `ConnectTargetPickService.SubmitTargetPickAsync` via
// `IPlayerRepository.GetPlayersByNormalizedFullNameAsync`, never
// `PlayerNameIndex` (COMP-06/COMP-10 separation, ADR-0007); and
// `TargetPickPanel.tsx` now submits the selected suggestion's NAME.
//
// This endpoint still seeds a matching `PlayerNameIndex` row below for each
// target player — not for id-space correctness anymore (the submitted value
// is a name, and resolution never touches `PlayerNameIndex`), but because
// `TargetPickPanel.tsx`'s UI still only enables "Set target pick" after the
// player clicks a real `/players/autocomplete` suggestion (see
// `PlayerSearchField.tsx`): without a `PlayerNameIndex` row for these
// seeded target players, autocomplete would return no suggestions and the
// E2E spec's `submitTargetPick` helper (`play-connect.spec.ts`) would have
// nothing to select. The `PlayerId` value on these rows is no longer
// load-bearing — it is kept equal to the real `Player.Id` only because
// that's the simplest thing to write, not because anything downstream reads
// or requires it.
//
// Bug fix (2026-09-05, ADR-0107): the chain-step candidate field
// (ChainBuilder.tsx) now ALSO requires a real suggestion click, same as
// TargetPickPanel.tsx — see ConnectCandidateResolver's own doc comment for
// why a name-typed-and-submitted-without-selecting flow reintroduces the
// exact same-name-collision bug this story closes. The connector player
// seeded below therefore needs a PlayerNameIndex row too, not just the two
// target-pick players (this paragraph previously said otherwise, before
// that UX changed).
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

            // Seeded so /players/autocomplete has something to return for
            // these players' names — TargetPickPanel.tsx requires a real
            // suggestion click before submitting, and (bug fix, 2026-09-05,
            // ADR-0107) ChainBuilder.tsx's candidate field now requires one
            // too, for the identical reason (see ConnectCandidateResolver's
            // own doc comment on the same-name-collision bug this closes).
            // WikidataQid on each row is seeded equal to the corresponding
            // Player row's own value, so this E2E path exercises the real
            // QID-based resolution these screens now use — never a
            // stand-in that only reaches the name-only fallback — and
            // ConnectCandidateResolver's GetOrCreatePlayersByWikidataQidAsync
            // call resolves to this exact already-seeded Player, never
            // creates a second one.
            await playerNameIndexRepository.UpsertManyAsync(
                [
                    new PlayerNameIndex
                    {
                        PlayerId = targetA.Id,
                        PrimaryName = targetA.FullName,
                        NormalizedName = PlayerNameNormalizer.Normalize(targetA.FullName),
                        WikidataQid = targetA.WikidataQid,
                    },
                    new PlayerNameIndex
                    {
                        PlayerId = targetB.Id,
                        PrimaryName = targetB.FullName,
                        NormalizedName = PlayerNameNormalizer.Normalize(targetB.FullName),
                        WikidataQid = targetB.WikidataQid,
                    },
                    new PlayerNameIndex
                    {
                        PlayerId = connector.Id,
                        PrimaryName = connector.FullName,
                        NormalizedName = PlayerNameNormalizer.Normalize(connector.FullName),
                        WikidataQid = connector.WikidataQid,
                    },
                ],
                cancellationToken);

            return Results.Ok(new SeedConnectPlayersResponse(
                targetA.FullName, targetB.FullName, connector.FullName, clubOverlappingWithA, clubOverlappingWithB));
        });
    }
}

// TargetPlayerAName/TargetPlayerBName/ConnectorPlayerName: all three are
// searchable (via PlayerNameIndex, see this file's own top-of-file comment)
// and selectable through the real UI — TargetPickPanel.tsx and (bug fix,
// 2026-09-05, ADR-0107) ChainBuilder.tsx's candidate field both now require
// a real suggestion click, never a bare typed-and-submitted name. Club*
// (design change, 2026-09-04, REQ-1406, ADR-0104): the player no longer
// types a club at all — these are exposed purely so an E2E spec can assert
// the SERVER-computed matched club/years it should see rendered back,
// never something a caller submits.
public record SeedConnectPlayersResponse(
    string TargetPlayerAName,
    string TargetPlayerBName,
    string ConnectorPlayerName,
    string ClubOverlappingWithA,
    string ClubOverlappingWithB);
