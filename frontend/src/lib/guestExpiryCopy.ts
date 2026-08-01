// REQ-718 rules 2/3: a guest account is removed automatically after 7 days
// of inactivity, or after 30 days if never claimed, whichever comes first.
// This string is the single source of that player-facing copy — App.tsx's
// guest banner and SettingsScreen.tsx's guest claim section both import it
// rather than each hardcoding their own copy of these numbers, so the two
// thresholds above and this sentence can never drift out of sync with each
// other (REQ-718's UI addendum, rule 5's own explicit requirement: "if rule
// 2's or rule 3's threshold value ever changes, this copy must be updated
// in the same change").
export const GUEST_EXPIRY_COPY =
  'Guest accounts are removed after 7 days of inactivity, or after 30 days if never claimed — whichever comes first.';
