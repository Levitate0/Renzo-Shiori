"use client";
import * as React from "react";

/**
 * Named theme presets — each bundles a full background palette (via
 * `[data-theme]` on <html>, see globals.css) plus a default accent, and an
 * explicit light/dark mode (applied through next-themes). A custom accent can
 * override any preset's highlight color (inline --primary-h/s/l), independent
 * of the palette. "renzo" (base dark) and "daylight" (base light) need no
 * [data-theme] block — the defaults already are them.
 */
export interface ThemePreset {
  id: string;
  label: string;
  mode: "light" | "dark";
  /** Preview swatches. */
  bg: string;
  card: string;
  accent: string;
}

export const THEME_PRESETS: ThemePreset[] = [
  { id: "renzo", label: "Renzo", mode: "dark", bg: "hsl(20 14.3% 4.1%)", card: "hsl(24 9.8% 10%)", accent: "hsl(346.8 77.2% 49.8%)" },
  { id: "amoled", label: "AMOLED", mode: "dark", bg: "hsl(0 0% 0%)", card: "hsl(0 0% 7%)", accent: "hsl(346.8 77.2% 49.8%)" },
  { id: "midnight", label: "Midnight", mode: "dark", bg: "hsl(222 47% 8%)", card: "hsl(222 40% 13%)", accent: "hsl(217.2 91.2% 59.8%)" },
  { id: "sakura", label: "Sakura", mode: "dark", bg: "hsl(330 22% 7%)", card: "hsl(330 18% 12%)", accent: "hsl(340 82% 66%)" },
  { id: "matcha", label: "Matcha", mode: "dark", bg: "hsl(140 15% 6%)", card: "hsl(140 12% 11%)", accent: "hsl(142.1 70.6% 45.3%)" },
  { id: "ember", label: "Ember", mode: "dark", bg: "hsl(20 22% 6%)", card: "hsl(20 18% 11%)", accent: "hsl(24.6 95% 53.1%)" },
  { id: "ocean", label: "Ocean", mode: "dark", bg: "hsl(195 40% 7%)", card: "hsl(195 34% 12%)", accent: "hsl(172 66% 45%)" },
  { id: "daylight", label: "Daylight", mode: "light", bg: "hsl(0 0% 100%)", card: "hsl(180 8% 90%)", accent: "hsl(346.8 77.2% 49.8%)" },
];

const PRESET_KEY = "renzo-preset";
const ACCENT_KEY = "renzo-accent"; // "custom" = override; anything else = use preset accent
const CUSTOM_KEY = "renzo-accent-custom"; // "H S% L%"
const CHANGE_EVENT = "renzo-theme-changed";
const DEFAULT_PRESET = "renzo";
const DEFAULT_CUSTOM = "265 83% 58%";
const BASE_PRESETS = new Set(["renzo", "daylight"]);

export function presetById(id: string): ThemePreset {
  return THEME_PRESETS.find((t) => t.id === id) ?? THEME_PRESETS[0];
}

export function getPreset(): string {
  if (typeof window === "undefined") return DEFAULT_PRESET;
  const p = localStorage.getItem(PRESET_KEY);
  return THEME_PRESETS.some((t) => t.id === p) ? (p as string) : DEFAULT_PRESET;
}
export function isCustomAccent(): boolean {
  return typeof window !== "undefined" && localStorage.getItem(ACCENT_KEY) === "custom";
}
export function getCustomHsl(): string {
  if (typeof window === "undefined") return DEFAULT_CUSTOM;
  return localStorage.getItem(CUSTOM_KEY) || DEFAULT_CUSTOM;
}

/** Apply palette (data-theme) + accent (inline custom or the preset's) to <html>. */
export function applyThemeDom(preset: string, customOn: boolean, customHsl: string): void {
  if (typeof document === "undefined") return;
  const el = document.documentElement;
  if (BASE_PRESETS.has(preset)) el.removeAttribute("data-theme");
  else el.setAttribute("data-theme", preset);

  if (customOn) {
    const parts = customHsl.trim().split(/\s+/);
    if (parts.length === 3) {
      el.style.setProperty("--primary-h", parts[0]);
      el.style.setProperty("--primary-s", parts[1]);
      el.style.setProperty("--primary-l", parts[2]);
    }
    el.setAttribute("data-accent", "custom");
  } else {
    el.style.removeProperty("--primary-h");
    el.style.removeProperty("--primary-s");
    el.style.removeProperty("--primary-l");
    el.removeAttribute("data-accent");
  }
}

function emit() {
  window.dispatchEvent(new Event(CHANGE_EVENT));
}

/** Select a theme. Mode (light/dark) is applied separately via next-themes. */
export function setPreset(id: string): void {
  if (typeof window === "undefined") return;
  localStorage.setItem(PRESET_KEY, id);
  applyThemeDom(id, isCustomAccent(), getCustomHsl());
  emit();
}
export function setCustomAccent(hsl: string): void {
  if (typeof window === "undefined") return;
  localStorage.setItem(ACCENT_KEY, "custom");
  localStorage.setItem(CUSTOM_KEY, hsl);
  applyThemeDom(getPreset(), true, hsl);
  emit();
}
/** Drop the custom accent override; the selected theme's accent applies. */
export function clearCustomAccent(): void {
  if (typeof window === "undefined") return;
  localStorage.setItem(ACCENT_KEY, "preset");
  applyThemeDom(getPreset(), false, getCustomHsl());
  emit();
}

interface ThemeState {
  preset: string;
  customOn: boolean;
  customHsl: string;
}

function snapshot(): string {
  return `${getPreset()}|${isCustomAccent() ? "1" : "0"}|${getCustomHsl()}`;
}
function subscribe(cb: () => void): () => void {
  window.addEventListener(CHANGE_EVENT, cb);
  window.addEventListener("storage", cb);
  return () => {
    window.removeEventListener(CHANGE_EVENT, cb);
    window.removeEventListener("storage", cb);
  };
}

export function useTheme(): ThemeState {
  const snap = React.useSyncExternalStore(subscribe, snapshot, () => `${DEFAULT_PRESET}|0|${DEFAULT_CUSTOM}`);
  return React.useMemo(() => {
    const [preset, custom, hsl] = snap.split("|");
    return { preset, customOn: custom === "1", customHsl: hsl };
  }, [snap]);
}

// hex <-> "H S% L%" helpers for the color input.
export function hslStrToHex(hsl: string): string {
  const [h, s, l] = hsl.trim().split(/\s+/);
  const H = parseFloat(h) / 360;
  const S = parseFloat(s) / 100;
  const L = parseFloat(l) / 100;
  const k = (n: number) => (n + H * 12) % 12;
  const a = S * Math.min(L, 1 - L);
  const f = (n: number) => L - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
  const toHex = (x: number) =>
    Math.round(x * 255)
      .toString(16)
      .padStart(2, "0");
  return `#${toHex(f(0))}${toHex(f(8))}${toHex(f(4))}`;
}
export function hexToHslStr(hex: string): string {
  const m = hex.replace("#", "");
  const r = parseInt(m.substring(0, 2), 16) / 255;
  const g = parseInt(m.substring(2, 4), 16) / 255;
  const b = parseInt(m.substring(4, 6), 16) / 255;
  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  let h = 0;
  const l = (max + min) / 2;
  const d = max - min;
  const s = d === 0 ? 0 : d / (1 - Math.abs(2 * l - 1));
  if (d !== 0) {
    if (max === r) h = ((g - b) / d) % 6;
    else if (max === g) h = (b - r) / d + 2;
    else h = (r - g) / d + 4;
    h *= 60;
    if (h < 0) h += 360;
  }
  return `${Math.round(h)} ${Math.round(s * 100)}% ${Math.round(l * 100)}%`;
}
