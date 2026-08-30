using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data;

// The single shared DbContext for every component (ADR-0014) — not just
// COMP-06 (Data.PlayerStore), despite the name predating that decision.
// Scoped to Tier 0: only the entities each backlog story has needed so far.
public class XGArcadeDbContext(DbContextOptions<XGArcadeDbContext> options) : DbContext(options)
{
    public DbSet<Player> Players => Set<Player>();
    public DbSet<PlayerData> PlayerData => Set<PlayerData>();
    public DbSet<PlayerOverride> PlayerOverrides => Set<PlayerOverride>();
    public DbSet<PlayerAttribute> PlayerAttributes => Set<PlayerAttribute>();
    public DbSet<PlayerAlias> PlayerAliases => Set<PlayerAlias>();
    // REQ-110 (2026-07-28 "persisted confirmed-low signal" extension) —
    // COMP-06 (Data.PlayerStore), same boundary as PlayerAttribute/PlayerData
    // above: only reachable from Games.XGGrid via IPlayerDataQualityRepository,
    // never a direct DbContext query. See ConfirmedLowMatchPair's own doc
    // comment for the full "why a new table" reasoning.
    public DbSet<ConfirmedLowMatchPair> ConfirmedLowMatchPairs => Set<ConfirmedLowMatchPair>();
    // REQ-110 (2026-08-01 "persistent technical-failure tracking"
    // extension, ADR-0052) — COMP-06, same boundary as ConfirmedLowMatchPair
    // above: only reachable from Games.XGGrid via IPlayerDataQualityRepository.
    // See PairLookupFailure's own doc comment for the full "why a separate
    // table from ConfirmedLowMatchPair" reasoning.
    public DbSet<PairLookupFailure> PairLookupFailures => Set<PairLookupFailure>();
    // ADR-0042/S-079 (COMP-06): xG Path's ordered, dated career-stint log —
    // see PlayerCareerStint's own doc comment. Never read by
    // IPlayerOverrideRepository's correctness-checking methods (xG Grid).
    public DbSet<PlayerCareerStint> PlayerCareerStints => Set<PlayerCareerStint>();
    // COMP-10 (Data.PlayerNameIndex) — see ADR-0007 and architecture-document.md
    // boundary rule 5. Deliberately never read by any COMP-06 repository;
    // only IPlayerNameIndexRepository queries this DbSet.
    public DbSet<PlayerNameIndex> PlayerNameIndexEntries => Set<PlayerNameIndex>();
    // REQ-208's 2026-07-26 correction / ADR-0044: per-word decomposition of
    // PlayerNameIndex.NormalizedName, indexed so a surname-only autocomplete
    // query can still be a proper (index-backed) StartsWith match. Same
    // COMP-10/autocomplete-only boundary as PlayerNameIndexEntries above —
    // never read by any COMP-06 repository.
    public DbSet<PlayerNameIndexWord> PlayerNameIndexWords => Set<PlayerNameIndexWord>();
    public DbSet<CountryDefinition> CountryDefinitions => Set<CountryDefinition>();
    public DbSet<ClubDefinition> ClubDefinitions => Set<ClubDefinition>();
    public DbSet<TrophyDefinition> TrophyDefinitions => Set<TrophyDefinition>();
    public DbSet<User> Users => Set<User>();
    public DbSet<GridTemplate> GridTemplates => Set<GridTemplate>();
    public DbSet<GridInstance> GridInstances => Set<GridInstance>();
    public DbSet<GridCell> GridCells => Set<GridCell>();
    // COMP-11 (Games.XGPath) — S-081's puzzle generation. Same
    // Template/Instance/Cell-equivalent shape as Games.XGGrid's own three
    // entities above.
    public DbSet<PathTemplate> PathTemplates => Set<PathTemplate>();
    public DbSet<PathInstance> PathInstances => Set<PathInstance>();
    public DbSet<PathPuzzle> PathPuzzles => Set<PathPuzzle>();
    // REQ-1208/ADR-0058: xG Path's own no-repeat-target-selection cycle
    // state — see PathTargetCycle/PathCycleTargetUsage's own doc comments.
    // Never read outside IPathInstanceRepository (Games.XGPath's own
    // persistence boundary, same as the three DbSets above).
    public DbSet<PathTargetCycle> PathTargetCycles => Set<PathTargetCycle>();
    public DbSet<PathCycleTargetUsage> PathCycleTargetUsages => Set<PathCycleTargetUsage>();
    // COMP-15 (Games.XGPredict)/ADR-0096 — REQ-1301/1302/1303's round/match/
    // prediction shape. Same Template/Instance/Cell-equivalent shape as
    // Games.XGGrid's/Games.XGPath's own entities above; PredictMatchPrediction
    // is the one exception (a separate top-level table, not an owned
    // collection) — see that entity's own doc comment for why.
    public DbSet<PredictTemplate> PredictTemplates => Set<PredictTemplate>();
    public DbSet<PredictInstance> PredictInstances => Set<PredictInstance>();
    public DbSet<PredictMatch> PredictMatches => Set<PredictMatch>();
    public DbSet<PredictMatchPrediction> PredictMatchPredictions => Set<PredictMatchPrediction>();
    public DbSet<Round> Rounds => Set<Round>();
    public DbSet<Guess> Guesses => Set<Guess>();
    public DbSet<League> Leagues => Set<League>();
    public DbSet<LeagueMembership> LeagueMemberships => Set<LeagueMembership>();

    // REQ-215/ADR-0052 (S-089): its own table, deliberately never joined
    // into or read alongside PlayerData/PlayerOverride/PlayerAttribute/
    // PlayerNameIndex above — see PlayerSuggestion's own doc comment.
    public DbSet<PlayerSuggestion> PlayerSuggestions => Set<PlayerSuggestion>();
    public DbSet<PlayerSuggestionClub> PlayerSuggestionClubs => Set<PlayerSuggestionClub>();

    // REQ-511: the site-wide announcement banner — see AnnouncementBanner's
    // own doc comment for why this table is a true singleton (at most one
    // row, ever) rather than a list/queue.
    public DbSet<AnnouncementBanner> AnnouncementBanners => Set<AnnouncementBanner>();

    // REQ-722/ADR-0087 (S-180): a player's profile-avatar upload pipeline —
    // see AvatarSubmission's own doc comment. No boundary relationship to
    // PlayerData/PlayerOverride/PlayerAttribute/PlayerNameIndex above; this
    // is a Core.Users-adjacent table, same "its own table" precedent
    // PlayerSuggestion already sets for a different concern.
    public DbSet<AvatarSubmission> AvatarSubmissions => Set<AvatarSubmission>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Dedup identity for players fetched across multiple intersection
        // queries — see Player.WikidataQid's doc comment. Filtered so
        // multiple NULLs (not-yet-resolved, Tier 1 non-Wikidata sources)
        // don't collide.
        modelBuilder.Entity<Player>()
            .HasIndex(p => p.WikidataQid)
            .IsUnique()
            .HasFilter("\"WikidataQid\" IS NOT NULL");

        // Grid generation's candidate-matching query (REQ-101) filters by
        // (AttributeType, AttributeValue).
        modelBuilder.Entity<PlayerAttribute>()
            .HasKey(pa => new { pa.PlayerId, pa.AttributeType, pa.AttributeValue });
        modelBuilder.Entity<PlayerAttribute>()
            .HasIndex(pa => new { pa.AttributeType, pa.AttributeValue });

        // Keyed on (PlayerId, NormalizedAlias) so re-running the same
        // Wikidata intersection query (§6a's skos:altLabel fetch) never
        // inserts a duplicate alias row for the same player.
        modelBuilder.Entity<PlayerAlias>()
            .HasKey(pa => new { pa.PlayerId, pa.NormalizedAlias });

        // REQ-110 (2026-07-28): composite key mirrors
        // CountPlayersWithBothAttributesAsync's own four-argument shape
        // exactly (see ConfirmedLowMatchPair's own doc comment) — a pair is
        // either marked confirmed-low or it isn't, so the natural-key lookup
        // this repository does (IsConfirmedLowAsync) is a straight PK hit,
        // no separate surrogate id/unique-index pair needed. No FK to
        // Player: unlike PlayerAttribute, a confirmed-low row often has NO
        // corresponding Player rows at all (the zero-match case this table
        // exists for) — there is nothing to reference.
        modelBuilder.Entity<ConfirmedLowMatchPair>()
            .HasKey(c => new { c.FirstAttributeType, c.FirstAttributeValue, c.SecondAttributeType, c.SecondAttributeValue });

        // StaleClubAttributeCleaner/purge-player-pool's clearing queries
        // filter by a single side's (AttributeType, AttributeValue) — e.g.
        // "every confirmed-low row involving this club, on either side of
        // the pair" — so both sides get their own index, same shape as
        // PlayerAttribute's (AttributeType, AttributeValue) index above. The
        // composite PK above already covers a First-side-only filter as a
        // leftmost-prefix match, but the Second side needs its own index to
        // avoid a full scan.
        modelBuilder.Entity<ConfirmedLowMatchPair>()
            .HasIndex(c => new { c.SecondAttributeType, c.SecondAttributeValue });

        // REQ-110 (2026-08-01, ADR-0052): same composite-key/index shape as
        // ConfirmedLowMatchPair above, for the same reasons — see
        // PairLookupFailure's own doc comment. No FK to Player for the same
        // reason as ConfirmedLowMatchPair: a pair that only ever technically
        // failed has no Player rows to reference.
        modelBuilder.Entity<PairLookupFailure>()
            .HasKey(f => new { f.FirstAttributeType, f.FirstAttributeValue, f.SecondAttributeType, f.SecondAttributeValue });

        modelBuilder.Entity<PairLookupFailure>()
            .HasIndex(f => new { f.SecondAttributeType, f.SecondAttributeValue });

        // PlayerData/PlayerOverride/PlayerAttribute/PlayerAlias all live
        // inside COMP-06 alongside Player, so (unlike ADR-0003's deliberate
        // cross-boundary FK omission) there's no reason to leave these
        // unconstrained — a row pointing at a nonexistent PlayerId is just
        // bad data.
        modelBuilder.Entity<PlayerData>()
            .HasOne<Player>()
            .WithMany()
            .HasForeignKey(pd => pd.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PlayerOverride>()
            .HasOne<Player>()
            .WithMany()
            .HasForeignKey(po => po.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PlayerAttribute>()
            .HasOne<Player>()
            .WithMany()
            .HasForeignKey(pa => pa.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PlayerAlias>()
            .HasOne<Player>()
            .WithMany()
            .HasForeignKey(pa => pa.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PlayerCareerStint>()
            .HasOne<Player>()
            .WithMany()
            .HasForeignKey(pcs => pcs.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        // ADR-0042/S-079: every future reader (xG Path's puzzle generation,
        // S-081+) needs "all of this player's stints" — the hot-path query
        // this table exists to serve.
        modelBuilder.Entity<PlayerCareerStint>()
            .HasIndex(pcs => pcs.PlayerId);

        // COMP-10 (Data.PlayerNameIndex, ADR-0007): keyed on PlayerId (the
        // bulk importer upserts in place per player, never inserting a
        // second row for the same player across re-runs — see
        // PlayerNameIndexImporter/IPlayerNameIndexRepository.UpsertManyAsync).
        // (NormalizedName) index per implementation-document.md §5's
        // required-indexes table — the autocomplete prefix search's hot path.
        // Deliberately no FK to Player: this index is bulk-imported from
        // Wikidata broadly (many players never generate a Player row at all
        // until/unless a live lookup or grid-generation cache write creates
        // one) — see ADR-0007's "separate data source" decision.
        modelBuilder.Entity<PlayerNameIndex>()
            .HasKey(pni => pni.PlayerId);
        modelBuilder.Entity<PlayerNameIndex>()
            .HasIndex(pni => pni.NormalizedName);

        // REQ-208's 2026-07-26 correction / ADR-0044: PlayerNameIndexWord is
        // PlayerNameIndex's own per-word decomposition (see that entity's doc
        // comment for why), so it's keyed and cascade-deleted against
        // PlayerNameIndex rather than Player — same "no FK crossing into
        // Player's id space" rule PlayerNameIndex itself follows. Composite
        // key (PlayerId, Word), same shape as PlayerAlias's (PlayerId,
        // NormalizedAlias) above, so re-upserting a player's words is
        // idempotent rather than duplicating. (Word) index is the actual hot
        // path — SearchByPrefixAsync's per-word StartsWith match.
        modelBuilder.Entity<PlayerNameIndexWord>()
            .HasKey(w => new { w.PlayerId, w.Word });
        modelBuilder.Entity<PlayerNameIndexWord>()
            .HasIndex(w => w.Word);
        modelBuilder.Entity<PlayerNameIndexWord>()
            .HasOne<PlayerNameIndex>()
            .WithMany()
            .HasForeignKey(w => w.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        // (Name) unique per implementation-document.md §5 — grid generation
        // picks from these directly (REQ-109); also prevents an admin
        // accidentally adding the same value twice under different casing
        // is out of scope for Tier 0 (no admin flow yet), but the
        // constraint itself is part of the baseline schema.
        modelBuilder.Entity<CountryDefinition>()
            .HasIndex(c => c.Name)
            .IsUnique();
        modelBuilder.Entity<ClubDefinition>()
            .HasIndex(c => c.Name)
            .IsUnique();
        modelBuilder.Entity<TrophyDefinition>()
            .HasIndex(t => t.Name)
            .IsUnique();

        // Every authenticated request resolves this first (implementation-
        // document.md §5's required-indexes table).
        modelBuilder.Entity<User>()
            .HasIndex(u => u.AuthProviderUserId)
            .IsUnique();

        // REQ-701: display names are unique case-insensitively — enforced
        // here (not just AuthController's pre-check) so a race between two
        // concurrent signups can't create two accounts with the same
        // displayed name. UserRepository.AddAsync's DbUpdateException catch
        // relies on this index's EF-generated name, "IX_Users_NormalizedDisplayName".
        modelBuilder.Entity<User>()
            .HasIndex(u => u.NormalizedDisplayName)
            .IsUnique();

        // GridInstance/GridCell are Games.XGGrid's (COMP-05) own entities —
        // Core never holds a foreign key to either (ADR-0003). Their own
        // internal relationship is a normal owned-collection FK, no
        // cross-component boundary concern.
        modelBuilder.Entity<GridCell>()
            .HasOne<GridInstance>()
            .WithMany(gi => gi.Cells)
            .HasForeignKey(gc => gc.GridInstanceId)
            .OnDelete(DeleteBehavior.Cascade);

        // A grid's guess-checking (S-009) always looks up a specific
        // (row, col) cell — REQ-101/102's generation loop never produces two
        // cells at the same coordinates within one instance.
        modelBuilder.Entity<GridCell>()
            .HasIndex(gc => new { gc.GridInstanceId, gc.Row, gc.Col })
            .IsUnique();

        // PathPuzzle/PathInstance are Games.XGPath's (COMP-11) own entities
        // — same normal owned-collection FK as GridCell/GridInstance above,
        // no ADR-0003 boundary concern (that ADR is specifically about
        // Round/Core never holding a game-specific FK).
        modelBuilder.Entity<PathPuzzle>()
            .HasOne<PathInstance>()
            .WithMany(pi => pi.Puzzles)
            .HasForeignKey(pp => pp.PathInstanceId)
            .OnDelete(DeleteBehavior.Cascade);

        // PathPuzzle.TargetPlayerId crosses into Player's table (COMP-06) —
        // see PathPuzzle's own doc comment for why this FK is meaningful
        // here (unlike GridCell, which has no single fixed per-cell answer).
        // Cascade mirrors every other Player-referencing FK above; there is
        // no player-row-deletion pathway in the codebase today.
        modelBuilder.Entity<PathPuzzle>()
            .HasOne<Player>()
            .WithMany()
            .HasForeignKey(pp => pp.TargetPlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        // REQ-1202: "no two puzzles in the same round instance target the
        // same player" — enforced at the DB level, same precedent as
        // GridCell's (GridInstanceId, Row, Col) unique index above.
        modelBuilder.Entity<PathPuzzle>()
            .HasIndex(pp => new { pp.PathInstanceId, pp.TargetPlayerId })
            .IsUnique();

        // REQ-1208/ADR-0058: PathCycleTargetUsage.PlayerId crosses into
        // Player's table (COMP-06) — same "meaningful FK, not ADR-0003's
        // Round/Core boundary concern" reasoning as PathPuzzle.
        // TargetPlayerId above. Cascade mirrors every other Player-
        // referencing FK; there is no player-row-deletion pathway today.
        modelBuilder.Entity<PathCycleTargetUsage>()
            .HasOne<Player>()
            .WithMany()
            .HasForeignKey(u => u.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        // REQ-1208: a player is recorded at most once per cycle — guards
        // AddInstanceWithCycleUsageAsync's insert the same way GridCell's
        // unique index above guards its own generation-time insert.
        modelBuilder.Entity<PathCycleTargetUsage>()
            .HasIndex(u => new { u.PlayerId, u.CycleNumber })
            .IsUnique();

        // GetUsedPlayerIdsInCycleAsync's hot-path read — every lookup is
        // scoped to "this cycle number", never PlayerId alone.
        modelBuilder.Entity<PathCycleTargetUsage>()
            .HasIndex(u => u.CycleNumber);

        // PredictMatch/PredictInstance are Games.XGPredict's (COMP-15) own
        // entities — same normal owned-collection FK as GridCell/GridInstance
        // and PathPuzzle/PathInstance above, no ADR-0003 boundary concern
        // (ADR-0096 §1).
        modelBuilder.Entity<PredictMatch>()
            .HasOne<PredictInstance>()
            .WithMany(pi => pi.Matches)
            .HasForeignKey(pm => pm.PredictInstanceId)
            .OnDelete(DeleteBehavior.Cascade);

        // ADR-0096 §2: PredictMatchPrediction.PredictMatchId is a real FK,
        // cascade — both tables are COMP-15-internal, no boundary reason to
        // leave this unconstrained (same reasoning as PredictMatch.
        // PredictInstanceId above). Deliberately NO FK for UserId — mirrors
        // Guess.UserId's own unconstrained shape (REQ-710 anonymization
        // precedent), confirmed by Guess's own registration above having no
        // HasForeignKey(g => g.UserId) call.
        modelBuilder.Entity<PredictMatchPrediction>()
            .HasOne<PredictMatch>()
            .WithMany()
            .HasForeignKey(pmp => pmp.PredictMatchId)
            .OnDelete(DeleteBehavior.Cascade);

        // REQ-1302: at most one PredictMatchPrediction row per (match, user)
        // — a resubmission overwrites this row, never inserts a second one.
        // Same precedent as Guess's own (RoundId, UserId, CellId) unique
        // index above.
        modelBuilder.Entity<PredictMatchPrediction>()
            .HasIndex(pmp => new { pmp.PredictMatchId, pmp.UserId })
            .IsUnique();

        // REQ-301's "one round ahead" check (GetLatestByGameKeyAsync) runs on
        // every scheduled generation invocation — the hot path for this table.
        modelBuilder.Entity<Round>()
            .HasIndex(r => new { r.GameKey, r.EndTime });

        // REQ-304: the actual race guard behind SequenceNumber's uniqueness
        // — RoundGenerationService's MAX(SequenceNumber)+1 read and this
        // row's insert are two separate round-trips, so this unique
        // constraint (not application code) is what makes two concurrent
        // generation attempts for the same GameKey unable to both succeed
        // with the same SequenceNumber; the loser's AddAsync fails instead.
        modelBuilder.Entity<Round>()
            .HasIndex(r => new { r.GameKey, r.SequenceNumber })
            .IsUnique();

        // REQ-208's guess-time name matching (S-009) queries this directly —
        // no PlayerNameIndex/COMP-10 in Tier 0 (MVP-SCOPE.md).
        modelBuilder.Entity<Player>()
            .HasIndex(p => p.NormalizedFullName);

        // REQ-201: at most one Guess row per (round, user, cell) — a
        // resubmission overwrites this row, never inserts a second one.
        modelBuilder.Entity<Guess>()
            .HasIndex(g => new { g.RoundId, g.UserId, g.CellId })
            .IsUnique();

        // REQ-204's uniqueness calculation (S-011) counts/groups by cell on
        // every read (implementation-document.md §5's required-indexes table).
        modelBuilder.Entity<Guess>()
            .HasIndex(g => g.CellId);

        // Round and Guess are both Core-owned tables in the same schema
        // (ADR-0014) — unlike Round.GameInstanceId's deliberate FK omission
        // (ADR-0003, a boundary against a *game-specific* table), there's no
        // boundary reason to leave this one unconstrained.
        modelBuilder.Entity<Guess>()
            .HasOne<Round>()
            .WithMany()
            .HasForeignKey(g => g.RoundId)
            .OnDelete(DeleteBehavior.Cascade);

        // REQ-401: at most one Type="global" League row can ever exist —
        // guards LeagueRepository.GetOrCreateGlobalLeagueAsync's
        // check-then-insert against a concurrent double-create, same
        // filtered-unique-index pattern as Player.WikidataQid.
        modelBuilder.Entity<League>()
            .HasIndex(l => l.Type)
            .IsUnique()
            .HasFilter($"\"Type\" = '{LeagueTypes.Global}'");

        // REQ-402: invite codes are globally unique across every custom
        // league — guards LeagueRepository.AddCustomLeagueAsync's
        // check-then-insert (behind LeagueService.CreateCustomLeagueAsync's
        // own InviteCodeExistsAsync pre-check) against a concurrent
        // double-create picking the same generated code, same
        // filtered-unique-index-adjacent pattern as League.Type above. No
        // HasFilter needed here (unlike Type's index): every Type="global"
        // League row has a null InviteCode, and Postgres treats multiple
        // NULLs in a unique index as non-colliding.
        modelBuilder.Entity<League>()
            .HasIndex(l => l.InviteCode)
            .IsUnique();

        // implementation-document.md §5's required-indexes table: leaderboard
        // queries filter by league, and this also enforces no duplicate
        // membership (REQ-401's "requires no action from the user" implies
        // signup never double-enrolls the same user).
        modelBuilder.Entity<LeagueMembership>()
            .HasKey(m => new { m.LeagueId, m.UserId });

        modelBuilder.Entity<LeagueMembership>()
            .HasOne<League>()
            .WithMany()
            .HasForeignKey(m => m.LeagueId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LeagueMembership>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // REQ-215/ADR-0052 (S-089): PlayerSuggestion.RoundId is a Core-owned
        // table in the same schema (ADR-0014) — same "no boundary reason to
        // leave this unconstrained" precedent as Guess.RoundId above. Unlike
        // Guess's own CellId/UserId (both deliberately unconstrained — see
        // PlayerSuggestion's own doc comment for why this table matches
        // that), Round itself is never a game-specific table, so a
        // PlayerSuggestion pointing at a nonexistent Round is just bad data.
        modelBuilder.Entity<PlayerSuggestion>()
            .HasOne<Round>()
            .WithMany()
            .HasForeignKey(ps => ps.RoundId)
            .OnDelete(DeleteBehavior.Cascade);

        // REQ-509/S-090's future admin queue lists pending suggestions —
        // same "status filter is the hot read path" precedent as PlayerData.
        // Confidence's implicit unverified-queue filter.
        modelBuilder.Entity<PlayerSuggestion>()
            .HasIndex(ps => ps.Status);

        // Owned-collection FK, same shape as GridCell/GridInstance and
        // PathPuzzle/PathInstance above — one PlayerSuggestion's asserted
        // clubs are deleted alongside it.
        modelBuilder.Entity<PlayerSuggestionClub>()
            .HasOne<PlayerSuggestion>()
            .WithMany(ps => ps.AssertedClubs)
            .HasForeignKey(psc => psc.PlayerSuggestionId)
            .OnDelete(DeleteBehavior.Cascade);

        // REQ-722 (S-180): AvatarSubmissionRepository's own two read paths
        // (GetPendingAsync/GetApprovedAsync) both filter on
        // SubmittingUserId + Status together — a composite index matches
        // that hot read path exactly, same "status filter is the hot read
        // path" precedent as PlayerSuggestion.Status's own index above,
        // narrowed further here since every read is also scoped to one
        // player.
        modelBuilder.Entity<AvatarSubmission>()
            .HasIndex(a => new { a.SubmittingUserId, a.Status });
    }
}
