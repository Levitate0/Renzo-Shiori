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
 *
 * "custom" is a user-picked hue: we set data-accent="custom" (which has no
 * stylesheet block, so all the non-hue vars fall back to base :root) and
 * inline-override --primary-h/s/l on <html> from the stored HSL. Switching
 * back to a preset removes those inline vars so the stylesheet rules win again.
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
export type AccentValue = AccentThemeId | "custom";

const STORAGE_KEY = "renzo-accent";
const CUSTOM_KEY = "renzo-accent-custom"; // "H S% L%", matching the CSS var format
const CHANGE_EVENT = "renzo-accent-changed";
const DEFAULT_ACCENT: AccentThemeId = "rose";
const DEFAULT_CUSTOM = "265 83% 58%"; // a pleasant violet, only used until picked

function isAccentId(value: string | null): value is AccentThemeId {
  return !!value && ACCENT_THEMES.some((t) => t.id === value);
}

function getAccent(): AccentValue {
  if (typeof window === "undefined") return DEFAULT_ACCENT;
  const stored = localStorage.getItem(STORAGE_KEY);
  if (stored === "custom") return "custom";
  return isAccentId(stored) ? stored : DEFAULT_ACCENT;
}

function getCustomHsl(): string {
  if (typeof window === "undefined") return DEFAULT_CUSTOM;
  return localStorage.getItem(CUSTOM_KEY) || DEFAULT_CUSTOM;
}

function applyAccent(accent: AccentValue, customHsl: string): void {
  const el = document.documentElement;
  if (accent === "custom") {
    const parts = customHsl.trim().split(/\s+/);
    if (parts.length === 3) {
      el.style.setProperty("--primary-h", parts[0]);
      el.style.setProperty("--primary-s", parts[1]);
      el.style.setProperty("--primary-l", parts[2]);
    }
    el.setAttribute("data-accent", "custom");
    return;
  }
  // Preset: drop any inline custom vars so the stylesheet rules take over.
  el.style.removeProperty("--primary-h");
  el.style.removeProperty("--primary-s");
  el.style.removeProperty("--primary-l");
  if (accent === DEFAULT_ACCENT) el.removeAttribute("data-accent");
  else el.setAttribute("data-accent", accent);
}

function setAccent(accent: AccentThemeId): void {
  if (typeof window === "undefined") return;
  localStorage.setItem(STORAGE_KEY, accent);
  applyAccent(accent, getCustomHsl());
  window.dispatchEvent(new Event(CHANGE_EVENT));
}

/** Sets a custom accent from an "H S% L%" string (see hexToHsl). */
function setCustomAccent(hsl: string): void {
  if (typeof window === "undefined") return;
  localStorage.setItem(CUSTOM_KEY, hsl);
  localStorage.setItem(STORAGE_KEY, "custom");
  applyAccent("custom", hsl);
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
 * Reactive hook for the accent picker. Returns the current accent, the current
 * custom HSL, and setters for presets and custom colors. All subscribed
 * components (and other tabs) update immediately on change.
 */
export function useAccentTheme(): {
  accent: AccentValue;
  customHsl: string;
  setPreset: (accent: AccentThemeId) => void;
  setCustom: (hsl: string) => void;
} {
  const accent = useSyncExternalStore(subscribe, getAccent, () => DEFAULT_ACCENT);
  const customHsl = useSyncExternalStore(subscribe, getCustomHsl, () => DEFAULT_CUSTOM);
  const setPreset = useCallback((next: AccentThemeId) => setAccent(next), []);
  const setCustom = useCallback((hsl: string) => setCustomAccent(hsl), []);
  return { accent, customHsl, setPreset, setCustom };
}

/** Converts a #rrggbb hex color to the "H S% L%" string the CSS vars expect. */
export function hexToHsl(hex: string): string {
  let r = 0, g = 0, b = 0;
  const m = hex.trim().replace("#", "");
  if (m.length === 3) {
    r = parseInt(m[0] + m[0], 16); g = parseInt(m[1] + m[1], 16); b = parseInt(m[2] + m[2], 16);
  } else if (m.length === 6) {
    r = parseInt(m.slice(0, 2), 16); g = parseInt(m.slice(2, 4), 16); b = parseInt(m.slice(4, 6), 16);
  }
  r /= 255; g /= 255; b /= 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b);
  let h = 0, s = 0;
  const l = (max + min) / 2;
  if (max !== min) {
    const d = max - min;
    s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    switch (max) {
      case r: h = (g - b) / d + (g < b ? 6 : 0); break;
      case g: h = (b - r) / d + 2; break;
      default: h = (r - g) / d + 4; break;
    }
    h /= 6;
  }
  return `${Math.round(h * 360)} ${Math.round(s * 100)}% ${Math.round(l * 100)}%`;
}

/** Converts an "H S% L%" string back to #rrggbb for the color-input value. */
export function hslToHex(hsl: string): string {
  const parts = hsl.trim().split(/\s+/);
  if (parts.length !== 3) return "#8b5cf6";
  const h = parseFloat(parts[0]) / 360;
  const s = parseFloat(parts[1]) / 100;
  const l = parseFloat(parts[2]) / 100;
  const hue2rgb = (p: number, q: number, t: number) => {
    if (t < 0) t += 1; if (t > 1) t -= 1;
    if (t < 1 / 6) return p + (q - p) * 6 * t;
    if (t < 1 / 2) return q;
    if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6;
    return p;
  };
  let r: number, g: number, b: number;
  if (s === 0) { r = g = b = l; }
  else {
    const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
    const p = 2 * l - q;
    r = hue2rgb(p, q, h + 1 / 3); g = hue2rgb(p, q, h); b = hue2rgb(p, q, h - 1 / 3);
  }
  const toHex = (x: number) => Math.round(x * 255).toString(16).padStart(2, "0");
  return `#${toHex(r)}${toHex(g)}${toHex(b)}`;
}
