/**
 * Platform bridge. The web build has no `window.__RENZO_NATIVE__`, so
 * `nativePrimitives()` returns null and every offline feature is a no-op /
 * hidden. The Capacitor and Electron shells inject an implementation at
 * startup (before the web bundle mounts).
 */
import type { NativePlatform, NativePrimitives } from "./types";
import { interfaceAdapter, type RawNativeInterface } from "./adapters";

const GLOBAL_KEY = "__RENZO_NATIVE__";

export function nativePrimitives(): NativePrimitives | null {
  if (typeof window === "undefined") return null;
  const w = window as unknown as Record<string, unknown>;

  // Already resolved (or a future shell that injects the full contract).
  const existing = w[GLOBAL_KEY];
  if (existing) return existing as NativePrimitives;

  // Build (and cache) an adapter from whichever raw shell interface is present.
  const android = w["__RenzoAndroid"] as RawNativeInterface | undefined;
  if (android) {
    const adapter = interfaceAdapter(android, "android");
    w[GLOBAL_KEY] = adapter;
    return adapter;
  }
  const windows = w["__RenzoWindows"] as RawNativeInterface | undefined;
  if (windows) {
    const adapter = interfaceAdapter(windows, "windows");
    w[GLOBAL_KEY] = adapter;
    return adapter;
  }
  return null;
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
