"use client";
import * as React from "react";
import { readerService, encodeFilename } from "@/lib/api/services/readerService";
import { getApiConfig } from "@/lib/api/config";
import { useToast } from "@/hooks/use-toast";
import { isNative } from "./bridge";
import { isChapterOffline } from "./offline";

/** Stable per-chapter id used across the manifest and reader. */
export function chapterKeyFor(seriesId: string, chapterNumber: number): string {
  return `${seriesId}:${chapterNumber}`;
}

interface DownloadTarget {
  seriesId: string;
  seriesTitle: string;
  chapterNumber: number;
  /** Server archive filename (downloaded library chapters only). */
  filename: string;
  // Series metadata cloned into the offline copy (raw paths; native adds auth).
  coverUrl?: string;
  description?: string;
  author?: string;
  status?: string;
}

function sessionToken(): string | null {
  try {
    return typeof window === "undefined" ? null : sessionStorage.getItem("renzo_token");
  } catch {
    return null;
  }
}

/**
 * Absolute server origin for the native downloader. getApiConfig().baseUrl is ""
 * when the app is same-origin with the server (the usual case in the WebView),
 * but the native HTTP client needs a full host — so fall back to the loaded
 * page's origin, which IS the server the WebView connected to.
 */
function serverBaseUrl(): string {
  const configured = getApiConfig().baseUrl;
  if (configured) return configured;
  return typeof window !== "undefined" ? window.location.origin : "";
}

/** Raw (un-tokened) page paths — the native downloader adds a Bearer header. */
function rawPagePaths(seriesId: string, filename: string, pageCount: number): string[] {
  const f = encodeFilename(filename);
  return Array.from({ length: pageCount }, (_, p) => `/api/reader/page?seriesId=${seriesId}&filename=${f}&page=${p}`);
}

function seriesMetaOf(t: DownloadTarget) {
  return {
    seriesId: t.seriesId,
    title: t.seriesTitle,
    coverPath: t.coverUrl ?? "",
    description: t.description ?? "",
    author: t.author ?? "",
  };
}

/** Hand a job to the native (background) downloader via the Android bridge. */
function enqueueNative(payload: unknown): boolean {
  const raw = (window as unknown as { __RenzoAndroid?: { enqueueDownload?: (s: string) => void } }).__RenzoAndroid;
  if (typeof raw?.enqueueDownload === "function") {
    try {
      raw.enqueueDownload(JSON.stringify(payload));
      return true;
    } catch {
      /* ignore */
    }
  }
  return false;
}

/**
 * Offline downloads via the native background service. The heavy fetching runs
 * in Kotlin (independent of the WebView), so it continues when the app is tabbed
 * out. This hook resolves each chapter's page count (a quick foreground call),
 * enqueues the job, and reflects progress from the native `renzo:download`
 * events (in-flight set, batch counter, and a tick the UI refreshes on).
 */
export function useOfflineDownload(): {
  downloadChapter: (t: DownloadTarget) => Promise<void>;
  downloadMany: (targets: DownloadTarget[]) => Promise<void>;
  inFlight: Set<string>;
  batch: { active: boolean; done: number; total: number };
  completedTick: number;
} {
  const { toast } = useToast();
  const [inFlight, setInFlight] = React.useState<Set<string>>(new Set());
  const [batch, setBatch] = React.useState({ active: false, done: 0, total: 0 });
  const [completedTick, setCompletedTick] = React.useState(0);

  React.useEffect(() => {
    if (!isNative()) return;
    const handler = (e: Event) => {
      const d = (e as CustomEvent<{ state?: string; chapterKey?: string; done?: number; total?: number }>).detail;
      if (!d) return;
      if (d.state === "saved" && d.chapterKey) {
        const key = d.chapterKey;
        setInFlight((prev) => {
          const n = new Set(prev);
          n.delete(key);
          return n;
        });
        setBatch((b) => (b.active ? { ...b, done: d.done ?? b.done, total: d.total ?? b.total } : b));
        setCompletedTick((t) => t + 1);
      } else if (d.state === "idle") {
        setBatch((b) => ({ ...b, active: false }));
        setCompletedTick((t) => t + 1);
      }
    };
    window.addEventListener("renzo:download", handler);
    return () => window.removeEventListener("renzo:download", handler);
  }, []);

  const downloadChapter = React.useCallback(
    async (t: DownloadTarget) => {
      if (!isNative()) return;
      const key = chapterKeyFor(t.seriesId, t.chapterNumber);
      if (await isChapterOffline(key)) {
        toast({ title: "Already saved", description: `Chapter ${t.chapterNumber} is on your device.` });
        return;
      }
      const token = sessionToken();
      if (!token) {
        toast({ title: "Sign in first", description: "Couldn't start the download.", variant: "destructive" });
        return;
      }
      try {
        const info = await readerService.getChapterInfo(t.seriesId, t.filename);
        const pageCount = info.pageCount ?? 0;
        if (!pageCount) throw new Error("This chapter has no downloadable pages.");
        setInFlight((prev) => new Set(prev).add(key));
        enqueueNative({
          baseUrl: serverBaseUrl(),
          token,
          series: seriesMetaOf(t),
          chapters: [{ chapterKey: key, chapterNumber: t.chapterNumber, pagePaths: rawPagePaths(t.seriesId, t.filename, pageCount) }],
        });
        toast({ title: "Downloading", description: `Chapter ${t.chapterNumber} — saving in the background.` });
      } catch (e) {
        toast({
          title: "Download failed",
          description: e instanceof Error ? e.message : "Couldn't start this download.",
          variant: "destructive",
        });
      }
    },
    [toast],
  );

  const downloadMany = React.useCallback(
    async (targets: DownloadTarget[]) => {
      if (!isNative() || targets.length === 0) return;
      const token = sessionToken();
      if (!token) {
        toast({ title: "Sign in first", description: "Couldn't start the download.", variant: "destructive" });
        return;
      }
      setBatch({ active: true, done: 0, total: targets.length });
      // Resolve page counts (quick, foreground) and build the batch job.
      const chapters: Array<{ chapterKey: string; chapterNumber: number; pagePaths: string[] }> = [];
      for (const t of targets) {
        const key = chapterKeyFor(t.seriesId, t.chapterNumber);
        if (await isChapterOffline(key)) continue;
        try {
          const info = await readerService.getChapterInfo(t.seriesId, t.filename);
          const pageCount = info.pageCount ?? 0;
          if (pageCount) {
            chapters.push({ chapterKey: key, chapterNumber: t.chapterNumber, pagePaths: rawPagePaths(t.seriesId, t.filename, pageCount) });
            setInFlight((prev) => new Set(prev).add(key));
          }
        } catch {
          /* skip a chapter we can't resolve */
        }
      }
      if (chapters.length === 0) {
        setBatch({ active: false, done: 0, total: 0 });
        toast({ title: "Nothing to download", description: "Those chapters are already saved." });
        return;
      }
      setBatch({ active: true, done: 0, total: chapters.length });
      enqueueNative({ baseUrl: serverBaseUrl(), token, series: seriesMetaOf(targets[0]), chapters });
      toast({ title: "Saving series offline", description: `${chapters.length} chapters — downloading in the background.` });
    },
    [toast],
  );

  return { downloadChapter, downloadMany, inFlight, batch, completedTick };
}
