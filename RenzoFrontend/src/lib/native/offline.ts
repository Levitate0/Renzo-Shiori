/**
 * Shared offline-download service. Platform-agnostic: it orchestrates *what* to
 * download and manages the manifest + purge policy, delegating raw file I/O and
 * the KV store to the injected native primitives. On web every call short-
 * circuits (no native → no-op), so the browser build is unaffected.
 */
import { nativePrimitives, requireNative } from "./bridge";
import type { OfflineChapter, OfflineManifest } from "./types";

const MANIFEST_KEY = "renzo.offline.manifest.v1";
const AUTOPURGE_KEY = "renzo.offline.autopurge";
const ROOT = "offline";

function sanitize(key: string): string {
  return key.replace(/[^a-zA-Z0-9._-]/g, "_");
}
function chapterDir(chapterKey: string): string {
  return `${ROOT}/${sanitize(chapterKey)}`;
}
function extFromContentType(ct: string | null): string {
  if (!ct) return "jpg";
  if (ct.includes("png")) return "png";
  if (ct.includes("webp")) return "webp";
  if (ct.includes("avif")) return "avif";
  if (ct.includes("gif")) return "gif";
  return "jpg";
}

// ── manifest ───────────────────────────────────────────────────────────────
export async function getManifest(): Promise<OfflineManifest> {
  const nat = nativePrimitives();
  if (!nat) return { version: 1, chapters: {} };
  const raw = await nat.kvGet(MANIFEST_KEY);
  if (!raw) return { version: 1, chapters: {} };
  try {
    const parsed = JSON.parse(raw) as OfflineManifest;
    if (parsed?.version === 1 && parsed.chapters) return parsed;
  } catch {
    /* corrupt manifest — start fresh */
  }
  return { version: 1, chapters: {} };
}

async function saveManifest(m: OfflineManifest): Promise<void> {
  await requireNative().kvSet(MANIFEST_KEY, JSON.stringify(m));
}

// ── auto-purge setting (user-facing toggle) ──────────────────────────────────
export function autoPurgeEnabled(): boolean {
  if (typeof window === "undefined") return true;
  return localStorage.getItem(AUTOPURGE_KEY) !== "off";
}
export function setAutoPurge(on: boolean): void {
  if (typeof window === "undefined") return;
  localStorage.setItem(AUTOPURGE_KEY, on ? "on" : "off");
}

// ── download ─────────────────────────────────────────────────────────────────
export interface SaveChapterInput {
  seriesId: string;
  chapterKey: string;
  chapterNumber: number;
  seriesTitle: string;
  /** Fully-qualified, auth-tokened page image URLs, in reading order. */
  pageUrls: string[];
}

export async function saveChapterOffline(
  input: SaveChapterInput,
  onProgress?: (done: number, total: number) => void,
): Promise<OfflineChapter> {
  const nat = requireNative();
  const dir = chapterDir(input.chapterKey);
  const pagePaths: string[] = [];
  let bytes = 0;
  const total = input.pageUrls.length;

  for (let i = 0; i < total; i++) {
    const res = await fetch(input.pageUrls[i]);
    if (!res.ok) throw new Error(`Page ${i + 1}/${total} failed (HTTP ${res.status})`);
    const buf = await res.arrayBuffer();
    const rel = `${dir}/${String(i).padStart(4, "0")}.${extFromContentType(res.headers.get("content-type"))}`;
    await nat.writeFile(rel, buf);
    pagePaths.push(rel);
    bytes += buf.byteLength;
    onProgress?.(i + 1, total);
  }

  const entry: OfflineChapter = {
    seriesId: input.seriesId,
    chapterKey: input.chapterKey,
    chapterNumber: input.chapterNumber,
    seriesTitle: input.seriesTitle,
    pageCount: total,
    pagePaths,
    bytes,
    savedAt: Date.now(),
  };
  const m = await getManifest();
  m.chapters[input.chapterKey] = entry;
  await saveManifest(m);
  return entry;
}

// ── query ────────────────────────────────────────────────────────────────────
export async function isChapterOffline(chapterKey: string): Promise<boolean> {
  return !!(await getManifest()).chapters[chapterKey];
}

/** Displayable <img> srcs for a saved chapter's pages, or null if not saved. */
export async function getOfflineChapterSrcs(chapterKey: string): Promise<string[] | null> {
  const nat = nativePrimitives();
  if (!nat) return null;
  const entry = (await getManifest()).chapters[chapterKey];
  if (!entry) return null;
  const srcs: string[] = [];
  for (const p of entry.pagePaths) srcs.push(await nat.readFileSrc(p));
  return srcs;
}

export async function listOffline(): Promise<OfflineChapter[]> {
  return Object.values((await getManifest()).chapters).sort((a, b) => b.savedAt - a.savedAt);
}

// ── delete / purge ───────────────────────────────────────────────────────────
export async function deleteOffline(chapterKey: string): Promise<void> {
  const nat = nativePrimitives();
  if (!nat) return;
  await nat.deletePath(chapterDir(chapterKey)).catch(() => {});
  const m = await getManifest();
  delete m.chapters[chapterKey];
  await saveManifest(m);
}

/**
 * Delete every saved chapter except optionally the one currently open — the
 * "back online → clean up, but don't yank what I'm reading" behavior. Returns
 * how many chapters were purged.
 */
export async function purgeAll(exceptChapterKey?: string): Promise<number> {
  const nat = nativePrimitives();
  if (!nat) return 0;
  const m = await getManifest();
  let purged = 0;
  for (const key of Object.keys(m.chapters)) {
    if (key === exceptChapterKey) continue;
    await nat.deletePath(chapterDir(key)).catch(() => {});
    delete m.chapters[key];
    purged++;
  }
  await saveManifest(m);
  return purged;
}
