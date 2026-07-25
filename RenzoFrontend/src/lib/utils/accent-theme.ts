"use client";

import { useSyncExternalStore, useCallback } from "react";

/**
 * Accent color theme — independent of the light/dark toggle (next-themes).
 * Every hardcoded "pink" value in globals.css derives from the CSS vars
 * --primary-h/--primary-s/--primary-l, which are overridden per-theme via
 * `[data-accent]` selectors on <html>. Switching the accent recolors the
 * whole app (primary buttons, focus rings, the Add Series spotlight, the
 * Sources page) in one shot.
 *
 * "rose" is the original brand color and has no [data-accent] override —
 * the base :root values in globals.css already are rose, so clearing the
 * attribute (or never setting it) falls back to rose for free.
 */
export const ACCENT_THEMES = [
  { id: "rose", label: "Rose", swatch: "hsl(346.8 77.2% 49.8%)" },
  { id: "blue", label: "Blue", swatch: "hsl(217.2 91.2% 59.8%)" },
  { id: "green", label: "Green", swatch: "hsl(142.1 70.6% 45.3%)" },
  { id: "purple", label: "Purple", swatch: "hsl(262.1 83.3% 57.8%)" },
  { id: "orange", label: "Orange", swatch: "hsl(24.6 95% 53.1%)" },
  { id: "slate", label: "Slate", swatch: "hsl(215 16% 46.9%)" },
] as const;

export type AccentThemeId = (typeof ACCENT_THEMES)[number]["id"];

const STORAGE_KEY = "renzo-accent";
const CHANGE_EVENT = "renzo-accent-changed";
const DEFAULT_ACCENT: AccentThemeId = "rose";

function isAccentId(value: string | null): value is AccentThemeId {
  return !!value && ACCENT_THEMES.some((t) => t.id === value);
}

function getAccent(): AccentThemeId {
  if (typeof window === "undefined") return DEFAULT_ACCENT;
  const stored = localStorage.getItem(STORAGE_KEY);
  return isAccentId(stored) ? stored : DEFAULT_ACCENT;
}

function applyAccent(accent: AccentThemeId): void {
  if (accent === DEFAULT_ACCENT) {
    document.documentElement.removeAttribute("data-accent");
  } else {
    document.documentElement.setAttribute("data-accent", accent);
  }
}

function setAccent(accent: AccentThemeId): void {
  if (typeof window === "undefined") return;
  localStorage.setItem(STORAGE_KEY, accent);
  applyAccent(accent);
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
 * Reactive hook for the accent color picker. All subscribed components
 * update immediately when any of them (or another tab) changes it.
 */
export function useAccentTheme(): [AccentThemeId, (accent: AccentThemeId) => void] {
  const accent = useSyncExternalStore(subscribe, getAccent, () => DEFAULT_ACCENT);
  const set = useCallback((next: AccentThemeId) => setAccent(next), []);
  return [accent, set];
}
