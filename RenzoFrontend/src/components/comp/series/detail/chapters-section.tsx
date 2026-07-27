"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeftRight, CheckCheck, Circle, CloudDownload, Download, ListChecks, Loader2, Search, Trash2, X } from "lucide-react";
import { Input } from "@/components/ui/input";
import { TooltipProvider } from "@/components/ui/tooltip";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogFooter,
  DialogTitle,
  DialogDescription,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { useToast } from "@/hooks/use-toast";
import {
  useDeleteDownloads,
  useDownloadAllChapters,
  useRedownloadChapter,
  useSeriesChapters,
} from "@/lib/api/hooks/useSeries";
import { useSettings } from "@/lib/api/hooks/useSettings";
import { readerService } from "@/lib/api/services/readerService";
import { seriesService } from "@/lib/api/services/seriesService";
import { cn } from "@/lib/utils";
import { useIsNative } from "@/lib/native/hooks";
import { useOfflineDownload, chapterKeyFor } from "@/lib/native/use-download";
import { listOffline } from "@/lib/native/offline";
import { ChapterRow } from "./chapter-row";

export interface ChaptersSectionProps {
  seriesId: string;
  /** Series-level pause flag — threaded from the detail page so it reflects live UI state. */
  paused: boolean;
  /** User may queue downloads. */
  canManage: boolean;
  /** Series info cloned into offline saves (cover/description/author) so the
   *  offline library looks like it does online. */
  seriesCoverUrl?: string;
  seriesDescription?: string;
  seriesAuthor?: string;
  seriesStatus?: string;
}

export function ChaptersSection({
  seriesId,
  paused,
  canManage,
  seriesCoverUrl,
  seriesDescription,
  seriesAuthor,
  seriesStatus,
}: ChaptersSectionProps) {
  const [missingOnly, setMissingOnly] = useState(false);
  const [query, setQuery] = useState("");
  const [pending, setPending] = useState<Set<number>>(new Set());
  const [readPending, setReadPending] = useState<Set<number>>(new Set());
  const [markingAll, setMarkingAll] = useState(false);
  // Multi-select mode
  const [selecting, setSelecting] = useState(false);
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [bulkPending, setBulkPending] = useState(false);
  const anchorRef = useRef<number | null>(null);
  // Delete-downloads confirmation. "all" = every downloaded chapter; "selected"
  // = the current multi-select. null = dialog closed.
  const [confirmDelete, setConfirmDelete] = useState<"all" | "selected" | null>(null);

  const { toast } = useToast();
  const router = useRouter();
  const queryClient = useQueryClient();
  const redownload = useRedownloadChapter();
  const downloadAll = useDownloadAllChapters();
  const deleteDownloads = useDeleteDownloads();
  const { data: chapters, isLoading, isError } = useSeriesChapters(seriesId, true);
  const { data: settings } = useSettings();
  const readerEnabled = settings?.readerEnabled !== false;

  // Read states from the built-in reader (progress, completion, bookmarks)
  const { data: readerChapters } = useQuery({
    queryKey: ["reader", "chapters", seriesId],
    queryFn: () => readerService.getChapters(seriesId),
    enabled: readerEnabled,
    staleTime: 30 * 1000,
  });
  const readStateByNumber = useMemo(() => {
    const map = new Map<number, { progress: number; isCompleted: boolean; bookmarked: boolean }>();
    readerChapters?.chapters.forEach((c) => map.set(c.number, c));
    return map;
  }, [readerChapters]);

  // ── Offline (native apps): save chapters to the device ──
  const native = useIsNative();
  const { downloadChapter, downloadMany, inFlight, batch } = useOfflineDownload();
  const filenameByNumber = useMemo(() => {
    const map = new Map<number, string>();
    readerChapters?.chapters.forEach((c) => {
      if (c.filename) map.set(c.number, c.filename);
    });
    return map;
  }, [readerChapters]);
  const [offlineSavedSet, setOfflineSavedSet] = useState<Set<number>>(new Set());
  const refreshOfflineSaved = useCallback(async () => {
    if (!native) return;
    const items = await listOffline();
    setOfflineSavedSet(new Set(items.filter((c) => c.seriesId === seriesId).map((c) => c.chapterNumber)));
  }, [native, seriesId]);
  useEffect(() => {
    void refreshOfflineSaved();
  }, [refreshOfflineSaved]);
  const offlineSeriesMeta = useMemo(
    () => ({
      coverUrl: seriesCoverUrl,
      description: seriesDescription,
      author: seriesAuthor,
      status: seriesStatus,
    }),
    [seriesCoverUrl, seriesDescription, seriesAuthor, seriesStatus],
  );
  const allDownloadedNumbers = useMemo(
    () => (chapters ?? []).filter((c) => c.downloaded && c.number != null).map((c) => c.number as number),
    [chapters],
  );
  const handleSaveOffline = useCallback(
    async (chapterNumber: number) => {
      const filename = filenameByNumber.get(chapterNumber);
      if (!filename || !readerChapters) return;
      await downloadChapter({ seriesId, seriesTitle: readerChapters.title, chapterNumber, filename, ...offlineSeriesMeta });
      void refreshOfflineSaved();
    },
    [filenameByNumber, readerChapters, downloadChapter, seriesId, refreshOfflineSaved, offlineSeriesMeta],
  );
  /** Batch-save many chapters for offline (a selection / whole series for a trip). */
  const handleBulkSaveOffline = useCallback(
    async (numbers: number[]) => {
      if (!readerChapters) return;
      const targets = numbers
        .map((n) => {
          const filename = filenameByNumber.get(n);
          return filename ? { seriesId, seriesTitle: readerChapters.title, chapterNumber: n, filename, ...offlineSeriesMeta } : null;
        })
        .filter((t): t is NonNullable<typeof t> => t !== null);
      await downloadMany(targets);
      void refreshOfflineSaved();
    },
    [filenameByNumber, readerChapters, downloadMany, seriesId, refreshOfflineSaved, offlineSeriesMeta],
  );

  // Opening a series kicks a stale-guarded source scan (backend skips providers
  // fetched within 15 min), and while the page is open its data re-pulls every
  // 20s — so new chapters / counts appear on every client without a manual
  // reload, even when SignalR push isn't getting through.
  useEffect(() => {
    if (canManage) {
      void seriesService.refreshSeries(seriesId, true).catch(() => { /* best effort */ });
    }
    const id = setInterval(() => {
      void queryClient.invalidateQueries({ queryKey: ["series", "chapters", seriesId] });
      void queryClient.invalidateQueries({ queryKey: ["series", "detail", seriesId] });
      void queryClient.invalidateQueries({ queryKey: ["reader", "chapters", seriesId] });
    }, 20000);
    return () => clearInterval(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [seriesId, canManage]);

  const handleRead = (chapterNumber: number) => {
    router.push(`/reader?seriesId=${seriesId}&chapter=${chapterNumber}`);
  };

  const readStateKey = ["reader", "chapters", seriesId];

  const handleToggleRead = async (chapterNumber: number, read: boolean) => {
    setReadPending((prev) => new Set(prev).add(chapterNumber));
    // Optimistically flip the read state so the row updates instantly.
    const previous = queryClient.getQueryData<typeof readerChapters>(readStateKey);
    queryClient.setQueryData<typeof readerChapters>(readStateKey, (old) =>
      old
        ? {
            ...old,
            chapters: old.chapters.map((c) =>
              c.number === chapterNumber
                ? { ...c, isCompleted: read, progress: read ? 1 : 0 }
                : c
            ),
          }
        : old
    );
    try {
      await readerService.markChapters(seriesId, [chapterNumber], read);
      // Re-sync with the server (also refreshes any derived reader state).
      void queryClient.invalidateQueries({ queryKey: readStateKey });
    } catch (err) {
      // Roll back the optimistic change and tell the user.
      queryClient.setQueryData(readStateKey, previous);
      toast({
        variant: "destructive",
        title: read ? "Couldn't mark as read" : "Couldn't mark as unread",
        description: err instanceof Error ? err.message : "Please try again.",
      });
    } finally {
      setReadPending((prev) => {
        const next = new Set(prev);
        next.delete(chapterNumber);
        return next;
      });
    }
  };

  const handleMarkAllRead = async () => {
    const numbers = (chapters ?? [])
      .map((c) => c.number)
      .filter((n): n is number => n != null);
    if (numbers.length === 0) return;
    setMarkingAll(true);
    const previous = queryClient.getQueryData<typeof readerChapters>(readStateKey);
    // Optimistically mark every chapter completed.
    queryClient.setQueryData<typeof readerChapters>(readStateKey, (old) =>
      old
        ? {
            ...old,
            chapters: old.chapters.map((c) => ({ ...c, isCompleted: true, progress: 1 })),
          }
        : old
    );
    try {
      await readerService.markChapters(seriesId, numbers, true);
      void queryClient.invalidateQueries({ queryKey: readStateKey });
      toast({
        variant: "success",
        title: "All chapters marked as read",
        description: `Marked ${numbers.length} chapter${numbers.length === 1 ? "" : "s"} as read.`,
      });
    } catch (err) {
      queryClient.setQueryData(readStateKey, previous);
      toast({
        variant: "destructive",
        title: "Couldn't mark all as read",
        description: err instanceof Error ? err.message : "Please try again.",
      });
    } finally {
      setMarkingAll(false);
    }
  };

  const total = chapters?.length ?? 0;
  const downloadedCount = chapters?.filter((c) => c.downloaded).length ?? 0;
  const missingCount = total - downloadedCount;

  const filtered = useMemo(() => {
    let list = chapters ?? [];
    if (missingOnly) list = list.filter((c) => !c.downloaded);
    const q = query.trim().toLowerCase();
    if (q) {
      list = list.filter(
        (c) =>
          (c.number != null && c.number.toString().includes(q)) ||
          c.name.toLowerCase().includes(q)
      );
    }
    return list;
  }, [chapters, missingOnly, query]);

  // ── Multi-select ──────────────────────────────────────────────────────
  // Selection operates over the chapters currently visible (after filter), so
  // "select all" / range / invert all mean "within what you can see".
  const filteredNumbers = useMemo(
    () => filtered.map((c) => c.number).filter((n): n is number => n != null),
    [filtered],
  );
  const selectedCount = selected.size;
  const allVisibleSelected = filteredNumbers.length > 0 && filteredNumbers.every((n) => selected.has(n));

  const exitSelection = () => {
    setSelecting(false);
    setSelected(new Set());
    anchorRef.current = null;
  };

  const handleSelectToggle = (chapterNumber: number, shiftKey: boolean) => {
    setSelected((prev) => {
      const next = new Set(prev);
      // Shift-click extends from the last-clicked row through this one.
      if (shiftKey && anchorRef.current != null) {
        const a = filteredNumbers.indexOf(anchorRef.current);
        const b = filteredNumbers.indexOf(chapterNumber);
        if (a !== -1 && b !== -1) {
          const [lo, hi] = a < b ? [a, b] : [b, a];
          for (let i = lo; i <= hi; i++) {
            const n = filteredNumbers[i];
            if (n != null) next.add(n);
          }
          return next;
        }
      }
      if (next.has(chapterNumber)) next.delete(chapterNumber);
      else next.add(chapterNumber);
      anchorRef.current = chapterNumber;
      return next;
    });
  };

  // Select all visible / clear all visible (one button).
  const handleSelectAllNone = () => {
    setSelected((prev) => {
      if (filteredNumbers.length > 0 && filteredNumbers.every((n) => prev.has(n))) {
        const next = new Set(prev);
        filteredNumbers.forEach((n) => next.delete(n));
        return next;
      }
      return new Set([...prev, ...filteredNumbers]);
    });
  };

  // Fill in every chapter between the first and last selected (in the visible order).
  const handleSelectBetween = () => {
    setSelected((prev) => {
      const idxs = filteredNumbers.map((n, i) => (prev.has(n) ? i : -1)).filter((i) => i >= 0);
      if (idxs.length < 1) return prev;
      const lo = Math.min(...idxs);
      const hi = Math.max(...idxs);
      const next = new Set(prev);
      for (let i = lo; i <= hi; i++) {
        const n = filteredNumbers[i];
        if (n != null) next.add(n);
      }
      return next;
    });
  };

  // Swap: invert the selection across the visible chapters.
  const handleInvertSelection = () => {
    setSelected((prev) => {
      const next = new Set(prev);
      filteredNumbers.forEach((n) => (next.has(n) ? next.delete(n) : next.add(n)));
      return next;
    });
  };

  const handleBulkMark = async (read: boolean) => {
    const numbers = [...selected];
    if (numbers.length === 0) return;
    setBulkPending(true);
    const numSet = new Set(numbers);
    const previous = queryClient.getQueryData<typeof readerChapters>(readStateKey);
    queryClient.setQueryData<typeof readerChapters>(readStateKey, (old) =>
      old
        ? {
            ...old,
            chapters: old.chapters.map((c) =>
              numSet.has(c.number) ? { ...c, isCompleted: read, progress: read ? 1 : 0 } : c
            ),
          }
        : old
    );
    try {
      await readerService.markChapters(seriesId, numbers, read);
      void queryClient.invalidateQueries({ queryKey: readStateKey });
      toast({
        variant: "success",
        title: read ? "Marked as read" : "Marked as unread",
        description: `${numbers.length} chapter${numbers.length === 1 ? "" : "s"} updated.`,
      });
      exitSelection();
    } catch (err) {
      queryClient.setQueryData(readStateKey, previous);
      toast({
        variant: "destructive",
        title: "Couldn't update read state",
        description: err instanceof Error ? err.message : "Please try again.",
      });
    } finally {
      setBulkPending(false);
    }
  };

  const handleBulkDownload = async () => {
    const numbers = [...selected];
    if (numbers.length === 0) return;
    setBulkPending(true);
    setPending((prev) => new Set([...prev, ...numbers]));
    const results = await Promise.allSettled(
      numbers.map((n) => redownload.mutateAsync({ seriesId, chapterNumber: n }))
    );
    const ok = results.filter((r) => r.status === "fulfilled").length;
    const failed = numbers.length - ok;
    setPending((prev) => {
      const next = new Set(prev);
      numbers.forEach((n) => next.delete(n));
      return next;
    });
    setBulkPending(false);
    if (ok > 0) {
      toast({
        variant: "success",
        title: "Downloads queued",
        description: `Queued ${ok} chapter${ok === 1 ? "" : "s"}${failed > 0 ? `, ${failed} couldn't be queued` : ""}.`,
      });
    } else {
      toast({
        variant: "destructive",
        title: "Couldn't queue downloads",
        description: "None of the selected chapters could be queued.",
      });
    }
    exitSelection();
  };

  const handleRedownload = (chapterNumber: number, providerId?: string) => {
    setPending((prev) => new Set(prev).add(chapterNumber));
    redownload.mutate(
      { seriesId, chapterNumber, providerId },
      {
        onSuccess: (res) => {
          toast({
            variant: "success",
            title: "Re-download queued",
            description: res.sourceProviderName
              ? `Queued chapter ${chapterNumber} from ${res.sourceProviderName}.`
              : `Queued chapter ${chapterNumber} for download.`,
          });
        },
        onError: (err) => {
          toast({
            variant: "destructive",
            title: "Re-download failed",
            description:
              err instanceof Error ? err.message : "Could not queue the chapter. Please try again.",
          });
        },
        onSettled: () => {
          setPending((prev) => {
            const next = new Set(prev);
            next.delete(chapterNumber);
            return next;
          });
        },
      }
    );
  };

  // Downloaded chapter numbers among the current selection — what a "delete
  // selected" would actually remove (selected rows with no file are ignored).
  const selectedDownloadedNumbers = useMemo(() => {
    const downloadedSet = new Set(
      (chapters ?? []).filter((c) => c.downloaded && c.number != null).map((c) => c.number as number),
    );
    return [...selected].filter((n) => downloadedSet.has(n));
  }, [chapters, selected]);

  const runDelete = async (scope: "all" | "selected") => {
    const numbers = scope === "all" ? undefined : selectedDownloadedNumbers;
    if (scope === "selected" && (!numbers || numbers.length === 0)) {
      setConfirmDelete(null);
      return;
    }
    try {
      const res = await deleteDownloads.mutateAsync({ seriesId, chapterNumbers: numbers });
      toast({
        variant: "success",
        title: "Downloads deleted",
        description:
          res.deleted > 0
            ? `Removed ${res.deleted} downloaded chapter${res.deleted === 1 ? "" : "s"} from disk.`
            : "No downloaded files were found to delete.",
      });
      if (scope === "selected") exitSelection();
    } catch (err) {
      toast({
        variant: "destructive",
        title: "Couldn't delete downloads",
        description: err instanceof Error ? err.message : "Please try again.",
      });
    } finally {
      setConfirmDelete(null);
    }
  };

  const handleDownloadAll = () => {
    downloadAll.mutate(seriesId, {
      onSuccess: (res) => {
        toast({
          variant: "success",
          title: "Downloading all chapters",
          description:
            res.queued > 0
              ? `Queued ${res.queued} missing chapter${res.queued === 1 ? "" : "s"}; also checking sources for any newer ones.`
              : "No missing chapters to queue — checking sources for anything new.",
        });
      },
      onError: (err) => {
        const msg = err instanceof Error ? err.message : "";
        toast({
          variant: "destructive",
          title: "Couldn't queue downloads",
          description: /pause/i.test(msg)
            ? "The series is paused — unpause it to download."
            : msg || "Please try again.",
        });
      },
    });
  };

  return (
    <section className="flex flex-col rounded-xl border border-border/60 bg-card/40 lg:h-full lg:min-h-0">
      {/* Header — always visible, reference-style "N chapters" with a read/download summary */}
      <div className="flex shrink-0 items-center justify-between gap-3 border-b border-border/60 px-4 py-3">
        <h2 className="text-base font-semibold tracking-tight">
          {total > 0 ? `${total} chapter${total === 1 ? "" : "s"}` : "Chapters"}
        </h2>
        {total > 0 && (
          <span className="text-xs text-muted-foreground">
            {downloadedCount} downloaded
            {missingCount > 0 && (
              <>
                {" · "}
                <span className="font-medium text-amber-500">{missingCount} missing</span>
              </>
            )}
          </span>
        )}
      </div>

          {isLoading && (
            <div className="flex items-center justify-center gap-2 py-10 text-sm text-muted-foreground">
              <Loader2 className="h-4 w-4 animate-spin" />
              Loading chapters…
            </div>
          )}

          {isError && (
            <div className="m-4 rounded-xl border border-dashed border-border/60 bg-card/50 p-8 text-center text-sm text-muted-foreground">
              Couldn&apos;t load chapters. Please try again.
            </div>
          )}

          {!isLoading && !isError && (
            <>
              {/* Controls — pinned above the scrolling list */}
              <div className="shrink-0 px-4 pt-4">
              <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                <div className="relative max-w-xs flex-1">
                  <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
                  <Input
                    value={query}
                    onChange={(e) => setQuery(e.target.value)}
                    placeholder="Filter by number or title…"
                    className="h-8 pl-8 text-sm"
                  />
                </div>
                <div className="flex items-center gap-2">
                  {canManage && missingCount > 0 && (
                    <button
                      type="button"
                      onClick={handleDownloadAll}
                      disabled={downloadAll.isPending}
                      title="Download every missing chapter"
                      className="inline-flex items-center gap-1.5 rounded-full border border-primary/40 bg-primary/10 px-3 py-1 text-[11px] font-medium text-primary transition-colors hover:bg-primary/20 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {downloadAll.isPending ? (
                        <Loader2 className="h-3.5 w-3.5 animate-spin" />
                      ) : (
                        <Download className="h-3.5 w-3.5" />
                      )}
                      Download all
                      <span className="tabular-nums opacity-80">({missingCount})</span>
                    </button>
                  )}
                  {canManage && downloadedCount > 0 && (
                    <button
                      type="button"
                      onClick={() => setConfirmDelete("all")}
                      disabled={deleteDownloads.isPending}
                      title="Delete every downloaded chapter file for this series"
                      className="inline-flex items-center gap-1.5 rounded-full border border-destructive/40 bg-destructive/10 px-3 py-1 text-[11px] font-medium text-destructive transition-colors hover:bg-destructive/20 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {deleteDownloads.isPending && confirmDelete === null ? (
                        <Loader2 className="h-3.5 w-3.5 animate-spin" />
                      ) : (
                        <Trash2 className="h-3.5 w-3.5" />
                      )}
                      Delete downloads
                      <span className="tabular-nums opacity-80">({downloadedCount})</span>
                    </button>
                  )}
                  {native && allDownloadedNumbers.length > 0 && (
                    <button
                      type="button"
                      onClick={() => void handleBulkSaveOffline(allDownloadedNumbers)}
                      disabled={batch.active}
                      title="Save the whole series to this device for offline reading"
                      className="inline-flex items-center gap-1.5 rounded-full border border-emerald-500/40 bg-emerald-500/10 px-3 py-1 text-[11px] font-medium text-emerald-400 transition-colors hover:bg-emerald-500/20 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {batch.active ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <CloudDownload className="h-3.5 w-3.5" />}
                      {batch.active ? `Saving ${batch.done}/${batch.total}` : "Save series offline"}
                      {!batch.active && <span className="tabular-nums opacity-80">({allDownloadedNumbers.length})</span>}
                    </button>
                  )}
                  {readerEnabled && total > 0 && (
                    <button
                      type="button"
                      onClick={handleMarkAllRead}
                      disabled={markingAll}
                      title="Mark every chapter as read"
                      className="inline-flex items-center gap-1.5 rounded-full border border-border/40 bg-foreground/[0.04] px-3 py-1 text-[11px] font-medium text-muted-foreground transition-colors hover:bg-foreground/[0.06] hover:text-foreground disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {markingAll ? (
                        <Loader2 className="h-3.5 w-3.5 animate-spin" />
                      ) : (
                        <CheckCheck className="h-3.5 w-3.5" />
                      )}
                      Mark all read
                    </button>
                  )}
                  {readerEnabled && total > 0 && (
                    <button
                      type="button"
                      onClick={() => (selecting ? exitSelection() : setSelecting(true))}
                      aria-pressed={selecting}
                      title="Select chapters for bulk actions"
                      className={cn(
                        "inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-[11px] font-medium transition-colors",
                        selecting
                          ? "border-primary/40 bg-primary/15 text-primary"
                          : "border-border/40 bg-foreground/[0.04] text-muted-foreground hover:bg-foreground/[0.06] hover:text-foreground"
                      )}
                    >
                      <ListChecks className="h-3.5 w-3.5" />
                      Select
                    </button>
                  )}
                  <button
                    type="button"
                    onClick={() => setMissingOnly((v) => !v)}
                    aria-pressed={missingOnly}
                    className={cn(
                      "inline-flex items-center gap-1.5 rounded-full border px-3 py-1 text-[11px] font-medium transition-colors",
                      missingOnly
                        ? "border-amber-500/40 bg-amber-500/15 text-amber-500"
                        : "border-border/40 bg-foreground/[0.04] text-muted-foreground hover:bg-foreground/[0.06] hover:text-foreground"
                    )}
                  >
                    Missing only
                    {missingCount > 0 && (
                      <span className="tabular-nums opacity-80">({missingCount})</span>
                    )}
                  </button>
                </div>
              </div>
              </div>

              {/* Selection toolbar — appears in multi-select mode */}
              {selecting && (
                <div className="shrink-0 border-t border-border/60 bg-foreground/[0.02] px-4 py-2.5">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="mr-1 text-xs font-medium tabular-nums text-muted-foreground">
                      {selectedCount} selected
                    </span>
                    <button
                      type="button"
                      onClick={handleSelectAllNone}
                      className="inline-flex items-center gap-1.5 rounded-full border border-border/40 bg-foreground/[0.04] px-3 py-1 text-[11px] font-medium text-muted-foreground transition-colors hover:bg-foreground/[0.06] hover:text-foreground"
                    >
                      {allVisibleSelected ? "Deselect all" : "Select all"}
                    </button>
                    <button
                      type="button"
                      onClick={handleSelectBetween}
                      disabled={selectedCount < 1}
                      title="Select every chapter between the first and last selected"
                      className="inline-flex items-center gap-1.5 rounded-full border border-border/40 bg-foreground/[0.04] px-3 py-1 text-[11px] font-medium text-muted-foreground transition-colors hover:bg-foreground/[0.06] hover:text-foreground disabled:cursor-not-allowed disabled:opacity-50"
                    >
                      Select in-between
                    </button>
                    <button
                      type="button"
                      onClick={handleInvertSelection}
                      title="Swap: invert the selection"
                      className="inline-flex items-center gap-1.5 rounded-full border border-border/40 bg-foreground/[0.04] px-3 py-1 text-[11px] font-medium text-muted-foreground transition-colors hover:bg-foreground/[0.06] hover:text-foreground"
                    >
                      <ArrowLeftRight className="h-3.5 w-3.5" />
                      Swap
                    </button>

                    <div className="mx-1 h-4 w-px bg-border/60" />

                    {readerEnabled && (
                      <>
                        <button
                          type="button"
                          onClick={() => void handleBulkMark(true)}
                          disabled={selectedCount === 0 || bulkPending}
                          className="inline-flex items-center gap-1.5 rounded-full border border-border/40 bg-foreground/[0.04] px-3 py-1 text-[11px] font-medium text-muted-foreground transition-colors hover:bg-foreground/[0.06] hover:text-foreground disabled:cursor-not-allowed disabled:opacity-50"
                        >
                          <CheckCheck className="h-3.5 w-3.5" />
                          Mark read
                        </button>
                        <button
                          type="button"
                          onClick={() => void handleBulkMark(false)}
                          disabled={selectedCount === 0 || bulkPending}
                          className="inline-flex items-center gap-1.5 rounded-full border border-border/40 bg-foreground/[0.04] px-3 py-1 text-[11px] font-medium text-muted-foreground transition-colors hover:bg-foreground/[0.06] hover:text-foreground disabled:cursor-not-allowed disabled:opacity-50"
                        >
                          <Circle className="h-3.5 w-3.5" />
                          Mark unread
                        </button>
                      </>
                    )}
                    {canManage && (
                      <button
                        type="button"
                        onClick={() => void handleBulkDownload()}
                        disabled={selectedCount === 0 || bulkPending}
                        className="inline-flex items-center gap-1.5 rounded-full border border-primary/40 bg-primary/10 px-3 py-1 text-[11px] font-medium text-primary transition-colors hover:bg-primary/20 disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        {bulkPending ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Download className="h-3.5 w-3.5" />}
                        Download
                      </button>
                    )}
                    {native && (
                      <button
                        type="button"
                        onClick={() => void handleBulkSaveOffline(selectedDownloadedNumbers)}
                        disabled={selectedDownloadedNumbers.length === 0 || batch.active}
                        title="Save the selected downloaded chapters to this device for offline reading"
                        className="inline-flex items-center gap-1.5 rounded-full border border-emerald-500/40 bg-emerald-500/10 px-3 py-1 text-[11px] font-medium text-emerald-400 transition-colors hover:bg-emerald-500/20 disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        {batch.active ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <CloudDownload className="h-3.5 w-3.5" />}
                        {batch.active ? `Saving ${batch.done}/${batch.total}` : "Save offline"}
                        {!batch.active && selectedDownloadedNumbers.length > 0 && (
                          <span className="tabular-nums opacity-80">({selectedDownloadedNumbers.length})</span>
                        )}
                      </button>
                    )}
                    {canManage && (
                      <button
                        type="button"
                        onClick={() => setConfirmDelete("selected")}
                        disabled={selectedDownloadedNumbers.length === 0 || bulkPending || deleteDownloads.isPending}
                        title="Delete downloaded files for the selected chapters"
                        className="inline-flex items-center gap-1.5 rounded-full border border-destructive/40 bg-destructive/10 px-3 py-1 text-[11px] font-medium text-destructive transition-colors hover:bg-destructive/20 disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        <Trash2 className="h-3.5 w-3.5" />
                        Delete
                        {selectedDownloadedNumbers.length > 0 && (
                          <span className="tabular-nums opacity-80">({selectedDownloadedNumbers.length})</span>
                        )}
                      </button>
                    )}

                    <button
                      type="button"
                      onClick={exitSelection}
                      title="Exit selection"
                      className="ml-auto inline-flex items-center gap-1.5 rounded-full border border-border/40 bg-foreground/[0.04] px-3 py-1 text-[11px] font-medium text-muted-foreground transition-colors hover:bg-foreground/[0.06] hover:text-foreground"
                    >
                      <X className="h-3.5 w-3.5" />
                      Done
                    </button>
                  </div>
                </div>
              )}

              {/* Rows — scroll internally. On desktop the panel is a flex child
                  that fills the remaining column height (flex-1), so it always
                  fits the window and the page never scrolls. On mobile it falls
                  back to a dvh-based cap (accounts for browser chrome). */}
              <div className="overflow-y-auto px-4 pb-4 pt-3 max-h-[calc(100dvh-14rem)] lg:max-h-none lg:min-h-0 lg:flex-1">
              {filtered.length > 0 ? (
                <TooltipProvider delayDuration={200}>
                  <div className="space-y-2">
                    {filtered.map((chapter, index) => {
                      const rs = chapter.number != null ? readStateByNumber.get(chapter.number) : undefined;
                      return (
                        <ChapterRow
                          key={chapter.number ?? `idx-${index}`}
                          chapter={chapter}
                          paused={paused}
                          canManage={canManage}
                          isPending={chapter.number != null && pending.has(chapter.number)}
                          onRedownload={handleRedownload}
                          onRead={readerEnabled ? handleRead : undefined}
                          onToggleRead={readerEnabled ? handleToggleRead : undefined}
                          readPending={chapter.number != null && readPending.has(chapter.number)}
                          readProgress={rs?.progress}
                          readCompleted={rs?.isCompleted}
                          readBookmarked={rs?.bookmarked}
                          selecting={selecting}
                          selected={chapter.number != null && selected.has(chapter.number)}
                          onSelectToggle={handleSelectToggle}
                          onSaveOffline={
                            native && chapter.downloaded && chapter.number != null && filenameByNumber.has(chapter.number)
                              ? () => void handleSaveOffline(chapter.number!)
                              : undefined
                          }
                          offlineSaving={chapter.number != null && inFlight.has(chapterKeyFor(seriesId, chapter.number))}
                          offlineSaved={chapter.number != null && offlineSavedSet.has(chapter.number)}
                        />
                      );
                    })}
                  </div>
                </TooltipProvider>
              ) : (
                <div className="rounded-xl border border-dashed border-border/60 bg-card/50 p-8 text-center text-sm text-muted-foreground">
                  {total === 0
                    ? "No chapters tracked for this series yet."
                    : missingOnly
                      ? "No missing chapters — everything is downloaded."
                      : "No chapters match your filter."}
                </div>
              )}
              </div>
            </>
          )}

      {/* Delete-downloads confirmation */}
      <Dialog open={confirmDelete !== null} onOpenChange={(o) => { if (!o) setConfirmDelete(null); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>
              {confirmDelete === "all" ? "Delete all downloads?" : "Delete selected downloads?"}
            </DialogTitle>
            <DialogDescription>
              {confirmDelete === "all" ? (
                <>
                  This removes the downloaded files for all{" "}
                  <span className="font-medium text-foreground">{downloadedCount}</span> downloaded
                  chapter{downloadedCount === 1 ? "" : "s"} from disk. Chapter history is kept — you
                  can re-download them later.
                </>
              ) : (
                <>
                  This removes the downloaded files for{" "}
                  <span className="font-medium text-foreground">{selectedDownloadedNumbers.length}</span>{" "}
                  selected chapter{selectedDownloadedNumbers.length === 1 ? "" : "s"} from disk.
                  Chapter history is kept — you can re-download them later.
                </>
              )}
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirmDelete(null)} disabled={deleteDownloads.isPending}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              onClick={() => confirmDelete && void runDelete(confirmDelete)}
              disabled={deleteDownloads.isPending}
            >
              {deleteDownloads.isPending ? (
                <><Loader2 className="mr-2 h-4 w-4 animate-spin" /> Deleting…</>
              ) : (
                "Delete"
              )}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </section>
  );
}
