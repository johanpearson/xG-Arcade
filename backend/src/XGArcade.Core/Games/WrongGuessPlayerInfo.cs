namespace XGArcade.Core.Games;

// REQ-216/ADR-0057: IGameModule.ResolveWrongGuessPlayerAsync's return shape —
// the canonical name (and, independently, an optional photo) of a real
// PlayerNameIndex-matched player a locked, final-incorrect guess turned out
// to name. Only ever constructed when the guess string matched a real
// PlayerNameIndex candidate at all (ADR-0007) — a guess matching nothing
// there resolves this whole type to null, never an instance with a null
// PlayerName (REQ-216: no identity to show at all in that case).
//
// PhotoUrl is nullable independently of this type being non-null: resolving
// PlayerName only requires the PlayerNameIndex match (always resolvable,
// often cache-only via an already-known Player row), while PhotoUrl
// additionally requires ADR-0057's own Wikidata-only live lookup to resolve
// a P18 within its timeout. Null whenever that lookup times out, errors, or
// genuinely finds no photo — REQ-216's silent, graceful fallback (never a
// broken-image icon, never treated as a correctness signal — there is none
// left to compute for a guess already known to be wrong).
public record WrongGuessPlayerInfo(string PlayerName, string? PhotoUrl);
