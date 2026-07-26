import './SplashScreen.css';
import { Logo } from '../components/Logo';

export interface SplashScreenProps {
  onGetStarted: () => void;
}

// REQ-719: the unauthenticated landing screen shown before AuthScreen —
// every time the app determines there's no valid session (a first-ever
// visit, a reload, or a return from logout/account-deletion/a failed silent
// refresh), not only on a literal first visit. App.tsx is what enforces
// "every time" (no persisted "already seen it" flag; see its own
// showAuthScreen state comment).
//
// No SCREEN-xx spec exists for this in design-document.md yet — same gap
// AuthScreen.tsx/GameSelectScreen.tsx already flag in §7. Built with only
// the existing §2 token system (color/typography), no new values, no
// animation.
//
// **2026-07-26 update:** REQ-719 originally shipped with no image logo
// asset (explicitly scoped out at the time, "to be handled separately") —
// this direct follow-up request adds the shared `Logo` mark+wordmark
// (`frontend/src/components/Logo.tsx`, also used in App.tsx's header) in
// its place. The heading's accessible name is unchanged ("xG Arcade" — all
// three of "x"/"G"/"Arcade" are real text), so the existing REQ-719 test
// below still asserts the same heading name; only the visual presentation
// changed.
export function SplashScreen({ onGetStarted }: SplashScreenProps) {
  return (
    // data-testid: App.tsx's header also renders an "xG Arcade" heading
    // whenever this screen is showing (it does for AuthScreen too), so a
    // plain role/name query for this screen's own heading is ambiguous —
    // same reasoning HeaderNav's own `header-nav-toggle` testid comment
    // gives for sidestepping a similar query quirk.
    <div className="splash-screen" data-testid="splash-screen">
      <div className="splash-screen__content">
        <h1 className="splash-screen__title">
          <Logo />
        </h1>
        <p className="splash-screen__tagline">
          Guess the player from their country and club. Compete on the leaderboard.
        </p>
        {/* REQ-719: the single, unambiguous primary action on this screen —
            no competing action of equal visual weight sits beside it. */}
        <button type="button" className="splash-screen__cta" onClick={onGetStarted}>
          Log in or sign up
        </button>
      </div>
    </div>
  );
}
