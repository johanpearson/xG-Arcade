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
    }
}
