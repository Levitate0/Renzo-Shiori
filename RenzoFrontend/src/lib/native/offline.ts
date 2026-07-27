/**
 * Shared offline-download service. Platform-agnostic: it orchestrates *what* to
 * download and manages the manifest + purge policy, delegating raw file I/O and
 * the KV store to the injected native primitives. On web every call short-
 * circuits (no native → no-op), so the browser build is unaffected.
 */
import { nativePrimitives, requireNative } from "./bridge";
import type { NativePrimitives, OfflineChapter, OfflineManifest, OfflineSeries } from "./types";

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
  if (!nat) return { version: 2, series: {}, chapters: {} };
  const raw = await nat.kvGet(MANIFEST_KEY);
  if (!raw) return { version: 2, series: {}, chapters: {} };
  try {
    const parsed = JSON.parse(raw) as Partial<OfflineManifest> & { version?: number };
    if (parsed?.chapters) {
      // Migrate v1 (no series map) forward.
      return { version: 2, series: parsed.series ?? {}, chapters: parsed.chapters };
    }
  } catch {
    /* corrupt manifest — start fresh */
  }
  return { version: 2, series: {}, chapters: {} };
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

// ── series metadata (cloned so offline looks like online) ────────────────────
const COVERS = `${ROOT}/covers`;

export interface SaveSeriesInput {
  seriesId: string;
  title: string;
  /** Fully-qualified cover image URL (downloaded once). */
  coverUrl?: string;
  description?: string;
  author?: string;
  status?: string;
}

/** Clone a series' info + cover for offline. Cover is fetched once and reused. */
export async function saveSeriesMeta(input: SaveSeriesInput): Promise<void> {
  const nat = nativePrimitives();
  if (!nat) return;
  const m = await getManifest();
  let coverPath = m.series[input.seriesId]?.coverPath;
  if (!coverPath && input.coverUrl) {
    try {
      const res = await fetch(input.coverUrl);
      if (res.ok) {
        const buf = await res.arrayBuffer();
        const rel = `${COVERS}/${sanitize(input.seriesId)}.${extFromContentType(res.headers.get("content-type"))}`;
        await nat.writeFile(rel, buf);
        coverPath = rel;
      }
    } catch {
      /* cover is best-effort */
    }
  }
  m.series[input.seriesId] = {
    seriesId: input.seriesId,
    title: input.title,
    coverPath,
    description: input.description,
    author: input.author,
    status: input.status,
  };
  await saveManifest(m);
}

export interface OfflineSeriesView extends OfflineSeries {
  coverSrc?: string;
  chapterCount: number;
  bytes: number;
}

/** Downloaded series with a displayable cover + chapter aggregate (for the library). */
export async function getOfflineSeries(): Promise<OfflineSeriesView[]> {
  const nat = nativePrimitives();
  if (!nat) return [];
  const m = await getManifest();
  const agg = new Map<string, { count: number; bytes: number }>();
  for (const c of Object.values(m.chapters)) {
    const e = agg.get(c.seriesId) ?? { count: 0, bytes: 0 };
    e.count++;
    e.bytes += c.bytes;
    agg.set(c.seriesId, e);
  }
  const out: OfflineSeriesView[] = [];
  for (const s of Object.values(m.series)) {
    const a = agg.get(s.seriesId);
    if (!a) continue; // no chapters saved → don't list
    let coverSrc: string | undefined;
    if (s.coverPath) {
      try {
        coverSrc = await nat.readFileSrc(s.coverPath);
      } catch {
        /* cover missing */
      }
    }
    out.push({ ...s, coverSrc, chapterCount: a.count, bytes: a.bytes });
  }
  return out.sort((x, y) => x.title.localeCompare(y.title));
}

/** Drop series entries (and their covers) that no longer have any saved chapters. */
async function pruneOrphanSeries(m: OfflineManifest, nat: NativePrimitives): Promise<void> {
  const withChapters = new Set(Object.values(m.chapters).map((c) => c.seriesId));
  for (const sid of Object.keys(m.series)) {
    if (!withChapters.has(sid)) {
      const cp = m.series[sid].coverPath;
      if (cp) await nat.deletePath(cp).catch(() => {});
      delete m.series[sid];
    }
  }
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

export interface SeriesChapterDownload {
  chapterKey: string;
  chapterNumber: number;
  pageUrls: string[];
}

export interface BatchProgress {
  chapterIndex: number;   // 0-based
  chapterCount: number;
  chapterNumber: number;
  pageDone: number;
  pageTotal: number;
}

/**
 * Download many chapters of one series for offline (a whole series / a range —
 * "grab a few for the trip"). Sequential so a big grab doesn't hammer the
 * source; already-saved chapters are skipped. Returns how many were newly saved.
 */
export async function saveSeriesOffline(
  series: { seriesId: string; seriesTitle: string },
  chapters: SeriesChapterDownload[],
  onProgress?: (p: BatchProgress) => void,
): Promise<number> {
  requireNative();
  let saved = 0;
  for (let i = 0; i < chapters.length; i++) {
    const ch = chapters[i];
    if (await isChapterOffline(ch.chapterKey)) continue;
    await saveChapterOffline(
      {
        seriesId: series.seriesId,
        seriesTitle: series.seriesTitle,
        chapterKey: ch.chapterKey,
        chapterNumber: ch.chapterNumber,
        pageUrls: ch.pageUrls,
      },
      (pageDone, pageTotal) =>
        onProgress?.({
          chapterIndex: i,
          chapterCount: chapters.length,
          chapterNumber: ch.chapterNumber,
          pageDone,
          pageTotal,
        }),
    );
    saved++;
  }
  return saved;
}

/** Total bytes across all downloaded chapters (for a storage readout). */
export async function offlineBytes(): Promise<number> {
  return Object.values((await getManifest()).chapters).reduce((sum, c) => sum + c.bytes, 0);
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
  await pruneOrphanSeries(m, nat);
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
  await pruneOrphanSeries(m, nat);
  await saveManifest(m);
  return purged;
}
