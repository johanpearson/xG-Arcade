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
    // ADR-0042/S-079 (COMP-06): xG Path's ordered, dated career-stint log —
    // see PlayerCareerStint's own doc comment. Never read by
    // IPlayerStoreRepository's correctness-checking methods (xG Grid).
    public DbSet<PlayerCareerStint> PlayerCareerStints => Set<PlayerCareerStint>();
    // COMP-10 (Data.PlayerNameIndex) — see ADR-0007 and architecture-document.md
    // boundary rule 5. Deliberately never read by IPlayerStoreRepository
    // (COMP-06); only IPlayerNameIndexRepository queries this DbSet.
    public DbSet<PlayerNameIndex> PlayerNameIndexEntries => Set<PlayerNameIndex>();
    // REQ-208's 2026-07-26 correction / ADR-0044: per-word decomposition of
    // PlayerNameIndex.NormalizedName, indexed so a surname-only autocomplete
    // query can still be a proper (index-backed) StartsWith match. Same
    // COMP-10/autocomplete-only boundary as PlayerNameIndexEntries above —
    // never read by IPlayerStoreRepository.
    public DbSet<PlayerNameIndexWord> PlayerNameIndexWords => Set<PlayerNameIndexWord>();
    public DbSet<CountryDefinition> CountryDefinitions => Set<CountryDefinition>();
    public DbSet<ClubDefinition> ClubDefinitions => Set<ClubDefinition>();
    public DbSet<TrophyDefinition> TrophyDefinitions => Set<TrophyDefinition>();
    public DbSet<User> Users => Set<User>();
    public DbSet<GridTemplate> GridTemplates => Set<GridTemplate>();
    public DbSet<GridInstance> GridInstances => Set<GridInstance>();
    public DbSet<GridCell> GridCells => Set<GridCell>();
    public DbSet<Round> Rounds => Set<Round>();
    public DbSet<Guess> Guesses => Set<Guess>();
    public DbSet<League> Leagues => Set<League>();
    public DbSet<LeagueMembership> LeagueMemberships => Set<LeagueMembership>();

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

        // REQ-301's "one round ahead" check (GetLatestByGameKeyAsync) runs on
        // every scheduled generation invocation — the hot path for this table.
        modelBuilder.Entity<Round>()
            .HasIndex(r => new { r.GameKey, r.EndTime });

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
    }
}
