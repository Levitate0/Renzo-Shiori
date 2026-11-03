/**
 * Account-wide "auto-buy paid chapters" switch, kept in the per-user
 * `preferences` blob under `autoBuyPaidChapters` (read server-side by
 * UserPriorityPrefsExtensions.GetAutoBuyPaidChapters). A series can override it.
 * Writes round-trip the whole blob so no other key is lost.
 */
export function parseAutoBuyPref(json?: string | null): boolean {
  if (!json) return false;
  try {
    const p = JSON.parse(json) as Record<string, unknown>;
    return typeof p === "object" && !!p && p.autoBuyPaidChapters === true;
  } catch {
    return false;
  }
}

export function mergeAutoBuyPref(existingJson: string | null | undefined, enabled: boolean): string {
  let base: Record<string, unknown> = {};
  if (existingJson) {
    try {
      const parsed = JSON.parse(existingJson) as Record<string, unknown>;
      if (typeof parsed === "object" && parsed) base = parsed;
    } catch {
      // Malformed existing blob — start fresh rather than lose the update.
    }
  }
  return JSON.stringify({ ...base, autoBuyPaidChapters: enabled });
}
