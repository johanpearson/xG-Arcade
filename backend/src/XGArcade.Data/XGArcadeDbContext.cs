using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data;

// COMP-06 (Data.PlayerStore)'s DbContext. Scoped to S-003 (docs/backlog.md):
// only the entities Tier 0 needs so far. Other components' entities (Round,
// Guess, User, GridInstance, ...) are added by the stories that own them.
public class XGArcadeDbContext(DbContextOptions<XGArcadeDbContext> options) : DbContext(options)
{
    public DbSet<Player> Players => Set<Player>();
    public DbSet<PlayerData> PlayerData => Set<PlayerData>();
    public DbSet<PlayerOverride> PlayerOverrides => Set<PlayerOverride>();
    public DbSet<PlayerAttribute> PlayerAttributes => Set<PlayerAttribute>();
    public DbSet<CountryDefinition> CountryDefinitions => Set<CountryDefinition>();
    public DbSet<ClubDefinition> ClubDefinitions => Set<ClubDefinition>();
    public DbSet<TrophyDefinition> TrophyDefinitions => Set<TrophyDefinition>();
    public DbSet<User> Users => Set<User>();

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

        // PlayerData/PlayerOverride/PlayerAttribute all live inside COMP-06
        // alongside Player, so (unlike ADR-0003's deliberate cross-boundary
        // FK omission) there's no reason to leave these unconstrained — a
        // row pointing at a nonexistent PlayerId is just bad data.
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
    }
}
