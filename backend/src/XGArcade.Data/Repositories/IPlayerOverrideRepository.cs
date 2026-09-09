using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-06 (Data.PlayerStore), split from IPlayerStoreRepository (S-107, pure
// refactor — see docs/decisions/0067-player-store-repository-split.md for
// the full "why" shared with S-106's four sibling interfaces): the
// PlayerOverride concern, plus HasEffectiveAttributeAsync — the single
// override-wins-over-attribute check every correctness path must use (REQ-203),
// which stays here rather than on IPlayerAttributeRepository since it's
// fundamentally override-driven (checks for an override first, only falling
// through to PlayerAttribute when none exists). See IPlayerRepository's own
// doc comment for the shared "no facade" boundary note that applies
// identically here.
public interface IPlayerOverrideRepository
{
    Task<PlayerOverride?> GetOverrideAsync(Guid playerId, string field, CancellationToken cancellationToken = default);
    Task<PlayerOverride?> GetOverrideByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddOverrideAsync(PlayerOverride playerOverride, CancellationToken cancellationToken = default);
    Task UpdateOverrideAsync(PlayerOverride playerOverride, CancellationToken cancellationToken = default);
    Task<bool> DeleteOverrideAsync(Guid id, CancellationToken cancellationToken = default);

    // REQ-203: "an override always takes precedence over synced/unverified
    // data" — the single effective-data check every correctness path
    // (grid-generation's cache read is count-only and doesn't need this;
    // guess-checking, S-009, does) must use, so override precedence is
    // enforced in exactly one place (architecture-document.md's Data
    // integrity row).
    Task<bool> HasEffectiveAttributeAsync(
        Guid playerId, string attributeType, string attributeValue, CancellationToken cancellationToken = default);

    // REQ-1501 (xG Higher/Lower, ADR-0110): every player's EFFECTIVE COUNT
    // for a given AttributeType, extending HasEffectiveAttributeAsync's
    // override-wins-for-the-whole-type rule (ADR-0015) from "is this exact
    // value effective" to "how many distinct effective values does this
    // player have" — a PlayerOverride for (PlayerId, attributeType) makes
    // this player's effective count exactly 1 (the override's own single
    // Value), regardless of how many raw PlayerAttribute rows of that type
    // exist for them; otherwise it's the count of that player's distinct
    // cached PlayerAttribute.AttributeValue rows for the type. A player with
    // NEITHER an override NOR any raw rows of that type is simply absent
    // from the returned dictionary (never present with value 0) — REQ-1501's
    // "a specific player is valid... only if they have a non-null recorded
    // value" makes "no value" and "value of zero" different states, and this
    // dictionary's absent-key convention preserves that distinction the same
    // way IPlayerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync's
    // own absent-key convention does. Tier 0 scale (a hand-curated club
    // list, MVP-SCOPE.md) is small enough that loading every PlayerAttribute/
    // PlayerOverride row for the type and grouping in memory is fine — same
    // precedent as PlayerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync's
    // own in-memory GroupBy.
    Task<IReadOnlyDictionary<Guid, int>> GetEffectivePlayerCountsByAttributeTypeAsync(
        string attributeType, CancellationToken cancellationToken = default);
}
