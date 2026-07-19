import { DeleteAccountScreen } from '../auth/DeleteAccountScreen';
import './SettingsScreen.css';

export interface SettingsScreenProps {
  accessToken: string;
  isAdmin: boolean;
  onAccountDeleted: () => void;
  onCancel: () => void;
  onAuthError: () => void;
  onOpenAdmin: () => void;
}

// SCREEN-05a (design-document.md §3), REQ-713: the single "Settings" nav
// entry's destination, consolidating what used to be two standalone
// top-level header links — "Delete account" (REQ-710) and, admin-only,
// "Admin" (REQ-504) — into one screen. `DeleteAccountScreen` itself is
// rendered unmodified (same props, same component) so its own REQ-710
// behavior/tests are untouched by this relocation; this component only
// adds the surrounding "Settings" framing and, admin-only, a link out to
// `AdminScreen` (a link, not admin controls embedded inline here — REQ-713
// is explicit that Settings itself never gains admin UI of its own). A
// non-admin renders nothing from the `isAdmin` branch at all, matching
// REQ-504's existing "no visible entry point" guarantee for its own screen.
export function SettingsScreen({
  accessToken,
  isAdmin,
  onAccountDeleted,
  onCancel,
  onAuthError,
  onOpenAdmin,
}: SettingsScreenProps) {
  return (
    <div className="settings-screen">
      <h2 className="settings-screen__title">Settings</h2>

      {isAdmin && (
        <section className="settings-screen__section">
          <button type="button" className="settings-screen__admin-link" onClick={onOpenAdmin}>
            Admin
          </button>
        </section>
      )}

      <DeleteAccountScreen
        accessToken={accessToken}
        onAccountDeleted={onAccountDeleted}
        onCancel={onCancel}
        onAuthError={onAuthError}
      />
    </div>
  );
}
