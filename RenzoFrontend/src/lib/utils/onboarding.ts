"use client";

import { userService } from "@/lib/api/services/userService";
import { parseThemePrefs, serializeThemePrefs } from "@/lib/utils/theme-prefs";
import type { User } from "@/lib/api/types";

/**
 * First-run walkthrough gating. Whether a user has seen the tour is stored
 * per-account in their preferences blob (`onboardedVersion`), so it shows once
 * per account and follows them across devices — shown to the owner once server
 * setup is done, and to every new account on first sign-in.
 *
 * Bump {@link ONBOARDING_VERSION} to re-show the tour to everyone after a
 * material change (e.g. a big new feature worth re-introducing).
 */
export const ONBOARDING_VERSION = 2;

/** Custom event a "Replay the walkthrough" control dispatches to force it open. */
export const REPLAY_WALKTHROUGH_EVENT = "renzo:open-walkthrough";

export function hasSeenOnboarding(user: User | null | undefined): boolean {
  if (!user) return true; // nothing to show when signed out
  return (parseThemePrefs(user.preferences).onboardedVersion ?? 0) >= ONBOARDING_VERSION;
}

/** Persist that this user has finished (or skipped) the current walkthrough. */
export async function markOnboarded(user: User | null | undefined): Promise<void> {
  if (!user) return;
  const prefs = parseThemePrefs(user.preferences);
  if ((prefs.onboardedVersion ?? 0) >= ONBOARDING_VERSION) return;
  prefs.onboardedVersion = ONBOARDING_VERSION;
  try {
    await userService.updateMe({ preferences: serializeThemePrefs(prefs) });
  } catch {
    // Non-fatal: they just might see the tour again on next load.
  }
}

/** Open the walkthrough on demand (e.g. from a menu), regardless of the flag. */
export function replayWalkthrough(): void {
  if (typeof window !== "undefined") window.dispatchEvent(new CustomEvent(REPLAY_WALKTHROUGH_EVENT));
}
