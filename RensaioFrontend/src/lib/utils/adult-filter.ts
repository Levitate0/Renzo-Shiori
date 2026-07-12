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

const ADULT_TAGS = new Set([
  "hentai",
  "erotica",
  "adult",
  "smut",
  "pornographic",
  "porn",
  "18+",
  "r18",
  "r-18",
  "r18+",
  "r-18g",
  "nsfw",
]);

/** True when the series' genres/tags mark it as explicit adult (18+) content. */
export function isAdultSeries(genres?: string[] | null): boolean {
  if (!genres || genres.length === 0) return false;
  return genres.some((g) => ADULT_TAGS.has(g.trim().toLowerCase()));
}

/** True when a single tag name is an explicit adult rating. */
export function isAdultTag(tag: string): boolean {
  return ADULT_TAGS.has(tag.trim().toLowerCase());
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

const STORAGE_KEY = "rensaio_hide_adult";
const CHANGE_EVENT = "rensaio-hide-adult-changed";

function getHideAdult(): boolean {
  if (typeof window === "undefined") return false;
  return localStorage.getItem(STORAGE_KEY) === "1";
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
