/**
 * Platform bridge. The web build has no `window.__RENZO_NATIVE__`, so
 * `nativePrimitives()` returns null and every offline feature is a no-op /
 * hidden. The Capacitor and Electron shells inject an implementation at
 * startup (before the web bundle mounts).
 */
import type { NativePlatform, NativePrimitives } from "./types";

const GLOBAL_KEY = "__RENZO_NATIVE__";

export function nativePrimitives(): NativePrimitives | null {
  if (typeof window === "undefined") return null;
  const injected = (window as unknown as Record<string, unknown>)[GLOBAL_KEY];
  return (injected as NativePrimitives | undefined) ?? null;
}

export function isNative(): boolean {
  return nativePrimitives() !== null;
}

export function nativePlatform(): NativePlatform {
  return nativePrimitives()?.platform ?? "web";
}

/** Throws if called on web — use only behind an `isNative()` guard. */
export function requireNative(): NativePrimitives {
  const n = nativePrimitives();
  if (!n) throw new Error("Offline features are only available in the Renzo Shiori app.");
  return n;
}
