"use client";

import { useSyncExternalStore, useCallback } from "react";

/**
 * Temporary "hide adult content" toggle + genre-based classifier.
 *
 * Classification is tag/genre based and deliberately narrow: explicit adult
 * ratings only (hentai, erotica, smut, …). Ecchi/mature/suggestive are NOT
 * treated as adult — the feature targets 18+ content, not fanservice.
 *
 * The toggle is a client-side view filter, not a setting: state lives in
 * localStorage so "hidden" safely survives reloads, and flipping it back is
 * one click in the user menu. Nothing is deleted or changed server-side.
 */

// Explicit RATING/act tags only. Ecchi, mature, suggestive, seinen, josei and
// harem are deliberately absent — this targets 18+, not fanservice — as are
// orientation genres (yaoi/yuri/BL) and formats (doujinshi), which say what a
// work is about rather than how explicit it is.
//
// Kept in step with the server's AdultContentClassifier.cs and Renzo Hub's
// AdultFilter.kt. All three change together.
const ADULT_TAGS = new Set([
  // Ratings
  "hentai", "erotica", "erotic", "adult", "smut", "pornographic", "porn",
  "18+", "r18", "r-18", "r18+", "r-18g", "nsfw", "adult (18+)", "explicit",
  // Explicit acts/kinks — a source that tags "Blowjob" but not "Adult" would
  // otherwise pass straight through a hide-18+ filter.
  "blowjob", "double penetration", "sex toys", "sexual violence",
  "sexual abuse", "rape", "incest", "netorare", "ntr", "bdsm",
  "bestiality", "futanari", "shemale", "dickgirl", "milf", "nudity",
  // Sexualised minors — never reachable with 18+ hidden.
  "loli", "lolicon", "shota", "shotacon",
]);

/**
 * The comparable pieces of a raw tag. Sources dress the same rating up in
 * different ways and an exact-match set misses all of them: MangaDex-style
 * prefixes ("Content rating: Pornographic") and bundled alternatives
 * ("Futanari | Shemale | Dickgirl", "Fellatio/Blowjob"). Strip the prefix,
 * split the alternatives, and match each piece on its own.
 */
function tagParts(raw: string): string[] {
  let tag = raw.trim();
  const colon = tag.indexOf(":");
  if (colon > 0 && colon < tag.length - 1) {
    const prefix = tag.slice(0, colon).trim().toLowerCase();
    if (prefix === "content rating" || prefix === "rating" || prefix === "genre") {
      tag = tag.slice(colon + 1).trim();
    }
  }
  const parts = [tag];
  if (tag.includes("|") || tag.includes("/")) {
    for (const p of tag.split(/[|/]/)) {
      const t = p.trim();
      if (t) parts.push(t);
    }
  }
  return parts;
}

/** True when the series' genres/tags mark it as explicit adult (18+) content. */
export function isAdultSeries(genres?: string[] | null): boolean {
  if (!genres || genres.length === 0) return false;
  return genres.some((g) => isAdultTag(g));
}

/** True when a single tag name is an explicit adult rating. */
export function isAdultTag(tag: string): boolean {
  if (!tag) return false;
  return tagParts(tag).some((p) => ADULT_TAGS.has(p.toLowerCase()));
}

/**
 * Detection for series/catalog items: prefers the server-computed flag —
 * which aggregates tags across ALL of the item's sources plus the user's
 * manual 18+ override, catching content whose visible source ships no adult
 * tags — and falls back to the visible tags for older cached payloads.
 */
export function isAdultItem(item: { genre?: string[] | null; isNsfw?: boolean }): boolean {
  return item.isNsfw === true || isAdultSeries(item.genre);
}

const STORAGE_KEY = "renzo_hide_adult";
const CHANGE_EVENT = "renzo-hide-adult-changed";

/**
 * Defaults to HIDDEN. A fresh browser (or a fresh install of the native
 * shells, which mirror this key) shows nothing adult until the user asks for
 * it — the catalogue a source returns isn't under our control, so opt-in is
 * the only defensible default. Only an explicit "0" means "show".
 */
function getHideAdult(): boolean {
  if (typeof window === "undefined") return true;
  return localStorage.getItem(STORAGE_KEY) !== "0";
}

function setHideAdult(value: boolean): void {
  if (typeof window === "undefined") return;
  localStorage.setItem(STORAGE_KEY, value ? "1" : "0");
  window.dispatchEvent(new Event(CHANGE_EVENT));
}

function subscribe(callback: () => void): () => void {
  window.addEventListener(CHANGE_EVENT, callback);
  // Cross-tab sync: localStorage writes in other tabs fire `storage`.
  window.addEventListener("storage", callback);
  return () => {
    window.removeEventListener(CHANGE_EVENT, callback);
    window.removeEventListener("storage", callback);
  };
}

/**
 * Reactive hook for the hide-adult toggle. All subscribed components update
 * immediately when any of them (or another tab) flips it.
 */
export function useHideAdult(): [boolean, () => void] {
  const hidden = useSyncExternalStore(subscribe, getHideAdult, () => false);
  const toggle = useCallback(() => setHideAdult(!getHideAdult()), []);
  return [hidden, toggle];
}
