/**
 * Shared offline/native types. The web build never touches the native paths —
 * every consumer guards on `isNative()` (see ./bridge), so these types describe
 * a contract that only the Capacitor (Android) and Electron (desktop) shells
 * fulfil at runtime by injecting `window.__RENZO_NATIVE__`.
 */

export type NativePlatform = "web" | "android" | "windows";

/** One downloaded-for-offline chapter, tracked in the manifest. */
export interface OfflineChapter {
  seriesId: string;
  /** Stable per-chapter id, e.g. `${seriesId}:${chapterNumber}`. */
  chapterKey: string;
  chapterNumber: number;
  seriesTitle: string;
  pageCount: number;
  /** App-relative file paths of the saved page images, in reading order. */
  pagePaths: string[];
  /** Total bytes on disk. */
  bytes: number;
  /** Epoch ms when saved. */
  savedAt: number;
}

/** Series-level info cloned for offline so the library looks like it does online. */
export interface OfflineSeries {
  seriesId: string;
  title: string;
  /** Saved cover image (app-relative path), if downloaded. */
  coverPath?: string;
  description?: string;
  author?: string;
  status?: string;
}

export interface OfflineManifest {
  version: 2;
  series: Record<string, OfflineSeries>;
  chapters: Record<string, OfflineChapter>;
}

/**
 * Low-level native primitives each shell injects as `window.__RENZO_NATIVE__`.
 * Deliberately dumb: all offline *logic* (what to download, manifest, purge
 * policy, current-read guard) lives in the shared TS below — a shell only has to
 * implement file I/O, a tiny KV store, and network status. That's what keeps
 * "write once, both platforms use it" true.
 */
export interface NativePrimitives {
  platform: Exclude<NativePlatform, "web">;

  // Download location. Files are stored under the user-chosen folder if set,
  // else an app-private default. `pickFolder` opens the native folder picker
  // (Android SAF / desktop dialog) and returns a display label, or null if
  // cancelled. `getFolder` returns the current label, or null for the default.
  pickFolder(): Promise<string | null>;
  getFolder(): Promise<string | null>;

  // Filesystem (paths are app-relative; the shell picks the real base dir).
  writeFile(relPath: string, data: ArrayBuffer): Promise<void>;
  /** A displayable <img src> for a saved file (capacitor convertFileSrc / electron file://). */
  readFileSrc(relPath: string): Promise<string>;
  deletePath(relPath: string): Promise<void>;
  exists(relPath: string): Promise<boolean>;

  // Small KV store for the manifest (Preferences / a json file).
  kvGet(key: string): Promise<string | null>;
  kvSet(key: string, value: string): Promise<void>;

  // Network status.
  isOnline(): Promise<boolean>;
  /** Subscribe to connectivity changes; returns an unsubscribe fn. */
  onNetworkChange(cb: (online: boolean) => void): () => void;
}
