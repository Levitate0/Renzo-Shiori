"use client";
import * as React from "react";
import { readerService } from "@/lib/api/services/readerService";
import { useToast } from "@/hooks/use-toast";
import { isNative } from "./bridge";
import {
  isChapterOffline,
  saveChapterOffline,
  saveSeriesMeta,
  type BatchProgress,
} from "./offline";

/** Stable per-chapter id used across the manifest and reader. */
export function chapterKeyFor(seriesId: string, chapterNumber: number): string {
  return `${seriesId}:${chapterNumber}`;
}

// ── background download (Android foreground service) ─────────────────────────
// The download runs in this WebView's JS, which Android suspends when the app is
// backgrounded. Holding a foreground service (with a progress notification)
// keeps the process alive so the download continues when tabbed out. Ref-counted
// so overlapping downloads don't stop it early. No-op off Android.
let fgCount = 0;
function androidFg(method: "startDownloadService" | "updateDownloadService" | "stopDownloadService", text?: string): void {
  if (typeof window === "undefined") return;
  const a = (window as unknown as { __RenzoAndroid?: Record<string, (t: string) => void> }).__RenzoAndroid;
  const fn = a?.[method];
  if (typeof fn === "function") {
    try {
      fn(text ?? "");
    } catch {
      /* ignore */
    }
  }
}
function fgStart(text: string): void {
  fgCount++;
  androidFg("startDownloadService", text);
}
function fgUpdate(text: string): void {
  if (fgCount > 0) androidFg("updateDownloadService", text);
}
function fgStop(): void {
  fgCount = Math.max(0, fgCount - 1);
  if (fgCount === 0) androidFg("stopDownloadService");
}

interface DownloadTarget {
  seriesId: string;
  seriesTitle: string;
  chapterNumber: number;
  /** Server archive filename (downloaded library chapters only). */
  filename: string;
  // Series metadata — cloned into the offline copy on the first save so the
  // offline library shows a cover + info, not just chapter numbers.
  coverUrl?: string;
  description?: string;
  author?: string;
  status?: string;
}

function seriesMetaOf(t: DownloadTarget) {
  return {
    seriesId: t.seriesId,
    title: t.seriesTitle,
    coverUrl: t.coverUrl,
    description: t.description,
    author: t.author,
    status: t.status,
  };
}

/** Build the ordered page-image URLs for a downloaded library chapter. */
async function pageUrlsFor(seriesId: string, filename: string): Promise<string[]> {
  const info = await readerService.getChapterInfo(seriesId, filename);
  const pageCount = info.pageCount ?? 0;
  if (!pageCount) throw new Error("This chapter has no downloadable pages.");
  return Array.from({ length: pageCount }, (_, p) => readerService.pageUrl(seriesId, filename, p));
}

/**
 * Save chapters to the device for offline reading. Handles a single chapter or
 * a batch ("grab a few for the trip") with progress + toasts, and tracks which
 * chapter keys are in flight so the UI can show a spinner. No-op on web.
 */
export function useOfflineDownload(): {
  downloadChapter: (t: DownloadTarget) => Promise<void>;
  downloadMany: (targets: DownloadTarget[]) => Promise<void>;
  inFlight: Set<string>;
  batch: { active: boolean; done: number; total: number; progress: BatchProgress | null };
} {
  const { toast } = useToast();
  const [inFlight, setInFlight] = React.useState<Set<string>>(new Set());
  const [batch, setBatch] = React.useState<{
    active: boolean;
    done: number;
    total: number;
    progress: BatchProgress | null;
  }>({ active: false, done: 0, total: 0, progress: null });

  const mark = React.useCallback((key: string, on: boolean) => {
    setInFlight((prev) => {
      const next = new Set(prev);
      if (on) next.add(key);
      else next.delete(key);
      return next;
    });
  }, []);

  const downloadChapter = React.useCallback(
    async (t: DownloadTarget) => {
      if (!isNative()) return;
      const key = chapterKeyFor(t.seriesId, t.chapterNumber);
      if (await isChapterOffline(key)) {
        toast({ title: "Already saved", description: `Chapter ${t.chapterNumber} is on your device.` });
        return;
      }
      mark(key, true);
      fgStart(`Saving Ch. ${t.chapterNumber}…`);
      try {
        await saveSeriesMeta(seriesMetaOf(t));
        const pageUrls = await pageUrlsFor(t.seriesId, t.filename);
        await saveChapterOffline({
          seriesId: t.seriesId,
          seriesTitle: t.seriesTitle,
          chapterKey: key,
          chapterNumber: t.chapterNumber,
          pageUrls,
        });
        toast({ title: "Saved offline", description: `Chapter ${t.chapterNumber} · ${pageUrls.length} pages` });
      } catch (e) {
        toast({
          title: "Download failed",
          description: e instanceof Error ? e.message : "Couldn't save this chapter offline.",
          variant: "destructive",
        });
      } finally {
        mark(key, false);
        fgStop();
      }
    },
    [mark, toast],
  );

  const downloadMany = React.useCallback(
    async (targets: DownloadTarget[]) => {
      if (!isNative() || targets.length === 0) return;
      setBatch({ active: true, done: 0, total: targets.length, progress: null });
      fgStart(`Saving ${targets[0].seriesTitle}…`);
      let saved = 0;
      try {
        // Clone the series info + cover once for the whole batch.
        await saveSeriesMeta(seriesMetaOf(targets[0]));
        for (let i = 0; i < targets.length; i++) {
          const t = targets[i];
          const key = chapterKeyFor(t.seriesId, t.chapterNumber);
          if (await isChapterOffline(key)) {
            setBatch((b) => ({ ...b, done: i + 1 }));
            continue;
          }
          fgUpdate(`Saving ${t.seriesTitle} · ${i + 1}/${targets.length}`);
          mark(key, true);
          try {
            const pageUrls = await pageUrlsFor(t.seriesId, t.filename);
            await saveChapterOffline(
              {
                seriesId: t.seriesId,
                seriesTitle: t.seriesTitle,
                chapterKey: key,
                chapterNumber: t.chapterNumber,
                pageUrls,
              },
              (pageDone, pageTotal) =>
                setBatch((b) => ({
                  ...b,
                  progress: { chapterIndex: i, chapterCount: targets.length, chapterNumber: t.chapterNumber, pageDone, pageTotal },
                })),
            );
            saved++;
          } catch {
            /* keep going — one bad chapter shouldn't abort the trip download */
          } finally {
            mark(key, false);
            setBatch((b) => ({ ...b, done: i + 1 }));
          }
        }
      } finally {
        fgStop();
      }
      setBatch({ active: false, done: targets.length, total: targets.length, progress: null });
      toast({ title: "Offline download complete", description: `${saved} of ${targets.length} chapter${targets.length === 1 ? "" : "s"} saved.` });
    },
    [mark, toast],
  );

  return { downloadChapter, downloadMany, inFlight, batch };
}
