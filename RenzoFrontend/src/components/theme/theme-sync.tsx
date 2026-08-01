"use client";

import { useEffect, useRef } from "react";
import { useAuth } from "@/contexts/auth-context";
import { setPreset, setCustomAccent, clearCustomAccent } from "@/lib/utils/theme-preset";
import { parseThemePrefs } from "@/lib/utils/theme-prefs";

/**
 * Applies the signed-in user's saved appearance preferences (theme preset +
 * accent — the app is dark-only, no light/dark mode to sync) once per login,
 * so their look follows their ACCOUNT across devices rather than living only
 * in this browser's localStorage. Renders nothing.
 *
 * Local storage drives the pre-hydration bootstrap (no flash on same-device
 * reloads); this reconciles to the server value after auth resolves, which
 * matters on a fresh device. Guarded by user id so it never fights a change the
 * user just made.
 */
export function ThemeSync() {
  const { user } = useAuth();
  const hydratedFor = useRef<string | null>(null);

  useEffect(() => {
    if (!user) {
      hydratedFor.current = null;
      return;
    }
    if (hydratedFor.current === user.id) return;
    hydratedFor.current = user.id;

    const prefs = parseThemePrefs(user.preferences);
    // setPreset falls back to the default ("renzo") for an unrecognized id —
    // covers a legacy account that had the now-removed light "daylight" preset saved.
    if (prefs.preset) {
      setPreset(prefs.preset);
    }
    if (prefs.accent === "custom" && typeof prefs.accentCustom === "string") {
      setCustomAccent(prefs.accentCustom);
    } else {
      clearCustomAccent();
    }
  }, [user]);

  return null;
}
