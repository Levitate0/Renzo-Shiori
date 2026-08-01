/**
 * Priority-system preferences (default source order, the redownload-on-upgrade
 * toggle) are per-user, not instance-wide — each user has their own library,
 * own sources, own opinion on which source should win. They live in the same
 * per-user `preferences` JSON blob theme-prefs.ts already uses (see
 * UserPriorityPrefsExtensions.cs on the backend for the mirrored read/write).
 *
 * Reads here only look at the two keys this feature owns. Writes go through
 * `mergePriorityPrefs`, which round-trips the FULL existing blob as a plain
 * object and only overrides these two keys — so saving a priority change can
 * never clobber theme (or any other) keys already in there, the same
 * unknown-keys-preserved contract theme-prefs.ts follows.
 */
export interface PriorityPrefs {
  /** Default source-priority order, by provider display name (lowest index = highest priority). */
  defaultSourcePriorityOrder?: string[];
  /** Re-download a chapter when a higher-priority source newly gains it. */
  redownloadFromHigherPrioritySources?: boolean;
}

export function parsePriorityPrefs(json?: string | null): PriorityPrefs {
  if (!json) return {};
  try {
    const p = JSON.parse(json) as Record<string, unknown>;
    if (typeof p !== "object" || !p) return {};
    return {
      defaultSourcePriorityOrder: Array.isArray(p.defaultSourcePriorityOrder)
        ? p.defaultSourcePriorityOrder.filter((s: unknown): s is string => typeof s === "string")
        : undefined,
      redownloadFromHigherPrioritySources:
        typeof p.redownloadFromHigherPrioritySources === "boolean"
          ? p.redownloadFromHigherPrioritySources
          : undefined,
    };
  } catch {
    return {};
  }
}

export function mergePriorityPrefs(existingJson: string | null | undefined, updates: PriorityPrefs): string {
  let base: Record<string, unknown> = {};
  if (existingJson) {
    try {
      const parsed = JSON.parse(existingJson) as Record<string, unknown>;
      if (typeof parsed === "object" && parsed) base = parsed;
    } catch {
      // Malformed existing blob — start fresh rather than lose the update.
    }
  }
  return JSON.stringify({ ...base, ...updates });
}
