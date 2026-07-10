using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data;

// The single shared DbContext for every component (ADR-0014) — not just
// COMP-06 (Data.PlayerStore), despite the name predating that decision.
// Scoped to Tier 0: only the entities each backlog story has needed so far.
// Guess (COMP-04/Core.Scoring) is still not added — that's S-009's job.
public class XGArcadeDbContext(DbContextOptions<XGArcadeDbContext> options) : DbContext(options)
{
    public DbSet<Player> Players => Set<Player>();
    public DbSet<PlayerData> PlayerData => Set<PlayerData>();
    public DbSet<PlayerOverride> PlayerOverrides => Set<PlayerOverride>();
    public DbSet<PlayerAttribute> PlayerAttributes => Set<PlayerAttribute>();
    public DbSet<PlayerAlias> PlayerAliases => Set<PlayerAlias>();
    public DbSet<CountryDefinition> CountryDefinitions => Set<CountryDefinition>();
    public DbSet<ClubDefinition> ClubDefinitions => Set<ClubDefinition>();
    public DbSet<TrophyDefinition> TrophyDefinitions => Set<TrophyDefinition>();
    public DbSet<User> Users => Set<User>();
    public DbSet<GridTemplate> GridTemplates => Set<GridTemplate>();
    public DbSet<GridInstance> GridInstances => Set<GridInstance>();
    public DbSet<GridCell> GridCells => Set<GridCell>();
    public DbSet<Round> Rounds => Set<Round>();

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
    }
}
