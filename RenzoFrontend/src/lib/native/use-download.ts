"use client";
import * as React from "react";
import { readerService } from "@/lib/api/services/readerService";
import { useToast } from "@/hooks/use-toast";
import { isNative } from "./bridge";
import {
  isChapterOffline,
  saveChapterOffline,
  type BatchProgress,
} from "./offline";

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
      try {
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
      }
    },
    [mark, toast],
  );

  const downloadMany = React.useCallback(
    async (targets: DownloadTarget[]) => {
      if (!isNative() || targets.length === 0) return;
      setBatch({ active: true, done: 0, total: targets.length, progress: null });
      let saved = 0;
      for (let i = 0; i < targets.length; i++) {
        const t = targets[i];
        const key = chapterKeyFor(t.seriesId, t.chapterNumber);
        if (await isChapterOffline(key)) {
          setBatch((b) => ({ ...b, done: i + 1 }));
          continue;
        }
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
      setBatch({ active: false, done: targets.length, total: targets.length, progress: null });
      toast({ title: "Offline download complete", description: `${saved} of ${targets.length} chapter${targets.length === 1 ? "" : "s"} saved.` });
    },
    [mark, toast],
  );

  return { downloadChapter, downloadMany, inFlight, batch };
}
