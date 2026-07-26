"use client";

import { useEffect, useRef } from "react";
import { useTheme } from "next-themes";
import { useAuth } from "@/contexts/auth-context";
import { useAccentTheme, type AccentThemeId, ACCENT_THEMES } from "@/lib/utils/accent-theme";
import { parseThemePrefs } from "@/lib/utils/theme-prefs";

/**
 * Applies the signed-in user's saved appearance preferences (theme mode + accent)
 * once per login, so a user's look follows their ACCOUNT across devices/browsers
 * rather than living only in this browser's localStorage. Renders nothing.
 *
 * Local storage still drives the pre-hydration bootstrap (no flash on same-device
 * reloads); this only reconciles to the server value after auth resolves, which
 * matters on a fresh device where local defaults differ from the saved account
 * theme. Guarded by user id so it never fights a change the user just made.
 */
export function ThemeSync() {
  const { user } = useAuth();
  const { setTheme } = useTheme();
  const { setPreset, setCustom } = useAccentTheme();
  const hydratedFor = useRef<string | null>(null);

  useEffect(() => {
    if (!user) {
      hydratedFor.current = null;
      return;
    }
    if (hydratedFor.current === user.id) return;
    hydratedFor.current = user.id;

    const prefs = parseThemePrefs(user.preferences);
    if (prefs.theme === "light" || prefs.theme === "dark" || prefs.theme === "system") {
      setTheme(prefs.theme);
    }
    if (prefs.accent === "custom" && typeof prefs.accentCustom === "string") {
      setCustom(prefs.accentCustom);
    } else if (prefs.accent && ACCENT_THEMES.some((t) => t.id === prefs.accent)) {
      setPreset(prefs.accent as AccentThemeId);
    }
  }, [user, setTheme, setPreset, setCustom]);

  return null;
}
