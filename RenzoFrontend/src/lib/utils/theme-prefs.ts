/**
 * Shape of the per-user appearance preferences persisted server-side (as a JSON
 * string on the User entity) and applied on login so a user's theme follows
 * their account across devices. Kept tiny and forward-compatible.
 */
export interface ThemePrefs {
  /** next-themes mode. */
  theme?: "light" | "dark" | "system";
  /** Named theme preset id (renzo/amoled/midnight/…); drives the palette. */
  preset?: string;
  /** Accent preset id, or "custom". */
  accent?: string;
  /** Custom accent as "H S% L%" (only meaningful when accent === "custom"). */
  accentCustom?: string;
}

export function parseThemePrefs(json?: string | null): ThemePrefs {
  if (!json) return {};
  try {
    const p = JSON.parse(json);
    return typeof p === "object" && p ? (p as ThemePrefs) : {};
  } catch {
    return {};
  }
}

export function serializeThemePrefs(p: ThemePrefs): string {
  return JSON.stringify(p);
}
