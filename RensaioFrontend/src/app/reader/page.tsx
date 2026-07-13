"use client";

/*
 * Built-in reader. Two data modes:
 *  - Library:  /reader?seriesId=…&chapter=<number>      (archives on disk; full progress tracking)
 *  - Preview:  /reader?mihonId=…&chapter=<index>&preview=1  (live pages via the source; nothing stored)
 *
 * Reading modes (Suwayomi-style):
 *  - auto      pick from the chapter's page shapes (server-side dims for library,
 *              client-side natural sizes for preview): mostly strip images → webtoon;
 *              >3 strip images mixed into normal pages (a converted long strip) →
 *              longstrip; otherwise paged (RTL when the series type is manga).
 *  - paged / paged-rtl / double / webtoon (continuous, no gaps) / longstrip
 *    (continuous, no gaps, width-matched) / vertical (continuous, with gaps)
 */

import React, { useCallback, useEffect, useMemo, useRef, useState, Suspense } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import {
  ArrowLeft, Bookmark, ChevronLeft, ChevronRight, Download, Settings2, X,
} from "lucide-react";
import { toast } from "sonner";

import { Button } from "@/components/ui/button";
import { Slider } from "@/components/ui/slider";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from "@/components/ui/select";
import { readerService } from "@/lib/api/services/readerService";
import { seriesService } from "@/lib/api/services/seriesService";
import { type ReaderChapters, type ReaderChapterInfo, type PreviewChapter } from "@/lib/api/types";

type ReaderMode = "auto" | "paged" | "paged-rtl" | "double" | "webtoon" | "longstrip" | "vertical";
type FitMode = "width" | "height" | "original";

interface ReaderSettings {
  mode: ReaderMode;
  fit: FitMode;
  maxWidthPct: number;     // % of viewport width cap in continuous modes
  background: "black" | "gray" | "white";
  preload: number;
  gapPx: number;           // vertical mode gap
  showPageNumber: boolean;
  tapNavigation: boolean;
  autoAdvance: boolean;    // jump to next chapter at the end
  autoMarkRead: boolean;
}

const DEFAULT_SETTINGS: ReaderSettings = {
  mode: "auto",
  fit: "width",
  maxWidthPct: 60,
  background: "black",
  preload: 4,
  gapPx: 12,
  showPageNumber: true,
  tapNavigation: true,
  autoAdvance: true,
  autoMarkRead: true,
};

const SETTINGS_KEY = "rensaio_reader_settings";
const seriesModeKey = (id: string) => `rensaio_reader_mode_${id}`;

function loadSettings(): ReaderSettings {
  if (typeof window === "undefined") return DEFAULT_SETTINGS;
  try {
    const raw = localStorage.getItem(SETTINGS_KEY);
    return raw ? { ...DEFAULT_SETTINGS, ...(JSON.parse(raw) as Partial<ReaderSettings>) } : DEFAULT_SETTINGS;
  } catch {
    return DEFAULT_SETTINGS;
  }
}

const BG: Record<ReaderSettings["background"], string> = {
  black: "#000",
  gray: "#18181b",
  white: "#fafafa",
};

function ReaderInner() {
  const params = useSearchParams();
  const router = useRouter();

  const isPreview = params.get("preview") === "1";
  const seriesId = params.get("seriesId");
  const mihonId = params.get("mihonId");
  const chapterParam = params.get("chapter");
  const previewTitle = params.get("title") ?? "Preview";

  const [settings, setSettings] = useState<ReaderSettings>(loadSettings);
  const [seriesModeOverride, setSeriesModeOverride] = useState<ReaderMode | null>(null);
  const [chapters, setChapters] = useState<ReaderChapters | null>(null);
  const [chapterNumber, setChapterNumber] = useState<number | null>(!isPreview && chapterParam ? parseFloat(chapterParam) : null);
  // Preview: source chapter lists are usually newest-first, so we keep a
  // reading-order (oldest-first) view and navigate through that. -1 = start
  // at the first chapter once the list arrives.
  const [previewChapterIndex, setPreviewChapterIndex] = useState<number>(isPreview && chapterParam ? parseInt(chapterParam) : 0);
  const [previewOrder, setPreviewOrder] = useState<PreviewChapter[] | null>(null);
  const [info, setInfo] = useState<ReaderChapterInfo | null>(null);
  const [pageCount, setPageCount] = useState(0);
  const [currentPage, setCurrentPage] = useState(0); // 0-based
  const [chromeVisible, setChromeVisible] = useState(true);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  // Name of the chapter being switched to — drives the "opening…" overlay so a
  // chapter change is never a silent blank screen, and the toast shown once it lands.
  const [openingLabel, setOpeningLabel] = useState<string | null>(null);
  const [arrivedLabel, setArrivedLabel] = useState<string | null>(null);
  // Client-side strip detection for preview mode (naturalWidth/Height as images load)
  const [detectedMode, setDetectedMode] = useState<"webtoon" | "longstrip" | "paged" | null>(null);
  const loadedDimsRef = useRef<Map<number, { w: number; h: number }>>(new Map());
  const scrollRef = useRef<HTMLDivElement>(null);
  const pageRefs = useRef<Map<number, HTMLDivElement>>(new Map());
  const progressSentRef = useRef<{ page: number; at: number }>({ page: -1, at: 0 });
  const markedReadRef = useRef(false);

  const persistSettings = useCallback((next: ReaderSettings) => {
    setSettings(next);
    try { localStorage.setItem(SETTINGS_KEY, JSON.stringify(next)); } catch { /* private mode */ }
  }, []);

  // ── Data loading ──────────────────────────────────────────────────────
  const chapter = useMemo(() => {
    if (!chapters || chapterNumber == null) return null;
    return chapters.chapters.find((c) => c.number === chapterNumber) ?? null;
  }, [chapters, chapterNumber]);

  const readableChapters = useMemo(
    () => (chapters?.chapters ?? []).filter((c) => !!c.filename),
    [chapters],
  );

  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      setLoading(true);
      setError(null);
      setCurrentPage(0);
      setInfo(null);
      setDetectedMode(null);
      loadedDimsRef.current.clear();
      markedReadRef.current = false;
      try {
        if (isPreview && mihonId) {
          let order = previewOrder;
          if (!order) {
            const list = await readerService.getPreviewChapters(mihonId);
            if (cancelled) return;
            const hasNumbers = list.chapters.some((c) => c.number != null);
            order = hasNumbers
              ? [...list.chapters].sort((a, b) => (a.number ?? 0) - (b.number ?? 0))
              : [...list.chapters].reverse();
            setPreviewOrder(order);
          }
          let idx = previewChapterIndex;
          if (idx < 0) {
            idx = order[0]?.index ?? 0;
            setPreviewChapterIndex(idx);
            return; // effect re-runs with the resolved index
          }
          const pages = await readerService.getPreviewPages(mihonId, idx);
          if (cancelled) return;
          setPageCount(pages.pageCount);
        } else if (seriesId && chapterNumber != null) {
          const ch = chapters ?? (await readerService.getChapters(seriesId));
          if (cancelled) return;
          if (!chapters) setChapters(ch);
          const target = ch.chapters.find((c) => c.number === chapterNumber);
          if (!target?.filename) {
            setError("This chapter is not downloaded.");
            setLoading(false);
            return;
          }
          const ci = await readerService.getChapterInfo(seriesId, target.filename);
          if (cancelled) return;
          setInfo(ci);
          setPageCount(ci.pageCount);
          // Resume where the user left off (not at 100%)
          if (target.progress > 0 && target.progress < 1 && ci.pageCount > 0) {
            setCurrentPage(Math.min(ci.pageCount - 1, Math.floor(target.progress * ci.pageCount)));
          }
          try {
            setSeriesModeOverride((localStorage.getItem(seriesModeKey(seriesId)) as ReaderMode) || null);
          } catch { /* ignore */ }
        } else {
          setError("Missing reader parameters.");
        }
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : "Failed to load chapter.");
      } finally {
        if (!cancelled) setLoading(false);
      }
    };
    void load();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isPreview, mihonId, seriesId, chapterNumber, previewChapterIndex]);

  // ── Mode resolution ───────────────────────────────────────────────────
  const resolvedMode: Exclude<ReaderMode, "auto"> = useMemo(() => {
    const chosen = seriesModeOverride ?? settings.mode;
    if (chosen !== "auto") return chosen;
    // Library: server-computed suggestion from archive page dimensions.
    if (info) {
      if (info.suggestedMode === "webtoon") return "webtoon";
      if (info.suggestedMode === "longstrip") return "longstrip";
      const isManga = (chapters?.type ?? "").toLowerCase().includes("manga");
      return isManga ? "paged-rtl" : "paged";
    }
    // Preview: decided from image natural sizes as they load.
    if (detectedMode === "webtoon") return "webtoon";
    if (detectedMode === "longstrip") return "longstrip";
    return "paged";
  }, [settings.mode, seriesModeOverride, info, detectedMode, chapters]);

  const isContinuous = resolvedMode === "webtoon" || resolvedMode === "longstrip" || resolvedMode === "vertical";
  const isRtl = resolvedMode === "paged-rtl";

  // Preview smart detection: after enough images report natural sizes, classify.
  const onImageLoaded = useCallback((index: number, w: number, h: number) => {
    if (!isPreview || detectedMode) return;
    loadedDimsRef.current.set(index, { w, h });
    const dims = [...loadedDimsRef.current.values()];
    if (dims.length >= Math.min(4, pageCount)) {
      const strips = dims.filter((d) => d.w > 0 && d.h / d.w >= 3).length;
      if (strips * 2 >= dims.length) setDetectedMode("webtoon");
      else if (strips > 3) setDetectedMode("longstrip");
      else setDetectedMode("paged");
    }
  }, [isPreview, detectedMode, pageCount]);

  /**
   * Height reservation for a page box in continuous mode, so the scroll height
   * is correct before any image has loaded. Library chapters carry exact
   * dimensions from the server; for preview we use whatever the image reported
   * once it loaded, and a tall-ish placeholder until then.
   */
  const pageAspect = useCallback((index: number): React.CSSProperties => {
    const dims = info?.pages.find((p) => p.index === index);
    if (dims?.width && dims?.height) {
      return { aspectRatio: `${dims.width} / ${dims.height}` };
    }
    const loaded = loadedDimsRef.current.get(index);
    if (loaded?.w && loaded?.h) {
      return { aspectRatio: `${loaded.w} / ${loaded.h}` };
    }
    return { minHeight: "70vh" };
  }, [info]);

  // ── Page URLs ─────────────────────────────────────────────────────────
  const pageUrl = useCallback((i: number): string => {
    if (isPreview && mihonId) return readerService.previewPageUrl(mihonId, previewChapterIndex, i);
    if (seriesId && chapter?.filename) return readerService.pageUrl(seriesId, chapter.filename, i);
    return "";
  }, [isPreview, mihonId, previewChapterIndex, seriesId, chapter]);

  // ── Progress reporting (library only) ─────────────────────────────────
  const reportProgress = useCallback((page0: number) => {
    if (isPreview || !seriesId || chapterNumber == null || pageCount === 0) return;
    const page1 = page0 + 1;
    const now = Date.now();
    if (progressSentRef.current.page >= page1 && now - progressSentRef.current.at < 30000) return;
    progressSentRef.current = { page: page1, at: now };
    void readerService.setProgress(seriesId, chapterNumber, page1, pageCount, chapter?.filename ?? undefined)
      .catch(() => { /* transient */ });
    if (settings.autoMarkRead && page1 >= pageCount && !markedReadRef.current) {
      markedReadRef.current = true;
      setChapters((prev) => prev && {
        ...prev,
        chapters: prev.chapters.map((c) => c.number === chapterNumber ? { ...c, isCompleted: true, progress: 1 } : c),
      });
    }
  }, [isPreview, seriesId, chapterNumber, pageCount, chapter, settings.autoMarkRead]);

  useEffect(() => { reportProgress(currentPage); }, [currentPage, reportProgress]);

  // Once the new chapter's pages are ready, swap the "Opening…" overlay for a
  // brief confirmation banner so the switch is never ambiguous.
  useEffect(() => {
    if (loading || !openingLabel) return;
    setArrivedLabel(openingLabel);
    setOpeningLabel(null);
    const t = setTimeout(() => setArrivedLabel(null), 2000);
    return () => clearTimeout(t);
  }, [loading, openingLabel]);

  // ── Navigation ────────────────────────────────────────────────────────
  const step = resolvedMode === "double" ? 2 : 1;

  /** True when another chapter exists in that direction (drives the end-of-chapter panel). */
  const hasChapter = useCallback((direction: 1 | -1): boolean => {
    if (isPreview) {
      if (!previewOrder) return false;
      const pos = previewOrder.findIndex((c) => c.index === previewChapterIndex);
      return !!previewOrder[pos + direction];
    }
    const idx = readableChapters.findIndex((c) => c.number === chapterNumber);
    return idx >= 0 && !!readableChapters[idx + direction];
  }, [isPreview, previewOrder, previewChapterIndex, readableChapters, chapterNumber]);

  const goToChapter = useCallback((direction: 1 | -1) => {
    if (isPreview) {
      if (!previewOrder) return;
      const pos = previewOrder.findIndex((c) => c.index === previewChapterIndex);
      const next = previewOrder[pos + direction];
      if (!next) {
        toast.info(direction > 0 ? "No next chapter." : "This is the first chapter.");
        return;
      }
      setOpeningLabel(next.name || `Chapter ${next.index + 1}`);
      setPreviewChapterIndex(next.index);
      return;
    }
    if (!chapters || chapterNumber == null) return;
    const idx = readableChapters.findIndex((c) => c.number === chapterNumber);
    const next = readableChapters[idx + direction];
    if (!next) {
      toast.info(direction > 0
        ? "That was the last downloaded chapter."
        : "This is the first downloaded chapter.");
      return;
    }
    setOpeningLabel(next.name || `Chapter ${next.number}`);
    setChapterNumber(next.number);
  }, [isPreview, previewOrder, previewChapterIndex, chapters, chapterNumber, readableChapters]);

  const advance = useCallback((dir: 1 | -1) => {
    // Chapter switching must happen here, not inside the setState updater —
    // React may invoke updaters twice, which would skip a chapter.
    const atEnd = dir > 0 && currentPage + step >= pageCount;
    if (atEnd) {
      if (settings.autoAdvance) goToChapter(1);
      return;
    }
    setCurrentPage((p) => Math.max(0, Math.min(pageCount - 1, p + dir * step)));
  }, [currentPage, pageCount, step, settings.autoAdvance, goToChapter]);

  // Keyboard
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return;
      if (isContinuous) {
        if (e.key === "ArrowLeft") goToChapter(-1);
        else if (e.key === "ArrowRight") goToChapter(1);
        return; // vertical scrolling handles the rest natively
      }
      const fwd = isRtl ? "ArrowLeft" : "ArrowRight";
      const back = isRtl ? "ArrowRight" : "ArrowLeft";
      if (e.key === fwd || e.key === " " || e.key === "PageDown") { e.preventDefault(); advance(1); }
      else if (e.key === back || e.key === "PageUp") { e.preventDefault(); advance(-1); }
      else if (e.key === "Home") setCurrentPage(0);
      else if (e.key === "End") setCurrentPage(Math.max(0, pageCount - 1));
      else if (e.key === "Escape") setChromeVisible((v) => !v);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [isContinuous, isRtl, advance, pageCount, goToChapter]);

  // Continuous mode: derive the current page from scroll position.
  //
  // This used to use an IntersectionObserver that took the highest intersecting
  // index. Lazy images have zero height until they load, so on open every page
  // box sits stacked at the top and they ALL intersect at once — which latched
  // the counter to the last page ("14/14") and, because progress follows the
  // page, instantly marked the chapter read. Measuring against a probe line
  // instead is immune to that, and the aspect-ratio boxes below mean the page
  // heights are right before a single image has loaded.
  useEffect(() => {
    if (!isContinuous || pageCount === 0 || loading) return;
    const scroller = scrollRef.current;
    if (!scroller) return;

    let frame = 0;
    const update = () => {
      frame = 0;
      const box = scroller.getBoundingClientRect();
      // A third of the way down the viewport: the page you'd say you're "on".
      const probe = box.top + scroller.clientHeight / 3;
      let current = 0;
      for (const [index, node] of pageRefs.current) {
        const rect = node.getBoundingClientRect();
        if (rect.top <= probe && rect.bottom > probe) { current = index; break; }
        if (rect.top > probe) break;      // past the probe — keep the last one below it
        current = index;
      }
      setCurrentPage(current);
    };
    const onScroll = () => {
      if (frame) return;                  // coalesce to one measure per frame
      frame = requestAnimationFrame(update);
    };

    scroller.addEventListener("scroll", onScroll, { passive: true });
    update();
    return () => {
      scroller.removeEventListener("scroll", onScroll);
      if (frame) cancelAnimationFrame(frame);
    };
  }, [isContinuous, pageCount, loading]);

  // Continuous mode: jump to the resume position once the pages are laid out.
  const restoredRef = useRef<string | null>(null);
  useEffect(() => {
    if (!isContinuous || loading || pageCount === 0) return;
    const key = `${seriesId ?? mihonId}:${chapterNumber ?? previewChapterIndex}`;
    if (restoredRef.current === key) return;
    restoredRef.current = key;
    if (currentPage > 0) pageRefs.current.get(currentPage)?.scrollIntoView();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isContinuous, loading, pageCount, seriesId, mihonId, chapterNumber, previewChapterIndex]);

  const handleTap = useCallback((e: React.MouseEvent<HTMLDivElement>) => {
    if (!settings.tapNavigation) { setChromeVisible((v) => !v); return; }
    const x = e.clientX / window.innerWidth;
    if (isContinuous) { setChromeVisible((v) => !v); return; }
    if (x < 0.3) advance(isRtl ? 1 : -1);
    else if (x > 0.7) advance(isRtl ? -1 : 1);
    else setChromeVisible((v) => !v);
  }, [settings.tapNavigation, isContinuous, isRtl, advance]);

  // ── Actions ───────────────────────────────────────────────────────────
  const toggleBookmark = useCallback(async () => {
    if (isPreview || !seriesId || chapterNumber == null || !chapter) return;
    const next = !chapter.bookmarked;
    try {
      await readerService.setBookmark(seriesId, chapterNumber, next);
      setChapters((prev) => prev && {
        ...prev,
        chapters: prev.chapters.map((c) => c.number === chapterNumber ? { ...c, bookmarked: next } : c),
      });
      toast.success(next ? "Bookmarked" : "Bookmark removed");
    } catch {
      toast.error("Failed to update bookmark");
    }
  }, [isPreview, seriesId, chapterNumber, chapter]);

  const downloadPreviewChapter = useCallback(async () => {
    // Downloading a specific chapter requires the series in the library —
    // the redownload endpoint then fetches exactly this one chapter.
    if (!seriesId) {
      toast.info("Add this series to your library first, then chapters can be downloaded individually.");
      return;
    }
    const num = previewOrder?.find((c) => c.index === previewChapterIndex)?.number;
    if (num == null) {
      toast.info("This chapter has no recognizable number — download it from the series page instead.");
      return;
    }
    try {
      await seriesService.redownloadChapter(seriesId, num);
      toast.success(`Chapter ${num} queued for download`);
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Failed to queue download");
    }
  }, [seriesId, previewOrder, previewChapterIndex]);

  // ── Rendering ─────────────────────────────────────────────────────────
  const containerWidthStyle = isContinuous
    ? { width: `min(100%, ${settings.maxWidthPct}vw)` }
    : undefined;

  const fitClass = settings.fit === "height"
    ? "max-h-screen w-auto"
    : settings.fit === "original"
      ? ""
      : "w-full h-auto";

  const gap = resolvedMode === "vertical" ? settings.gapPx : 0;

  const pagesToRender: number[] = useMemo(() => {
    if (isContinuous) return Array.from({ length: pageCount }, (_, i) => i);
    if (resolvedMode === "double") {
      const second = currentPage + 1 < pageCount ? [currentPage + 1] : [];
      return [currentPage, ...second];
    }
    return [currentPage];
  }, [isContinuous, resolvedMode, currentPage, pageCount]);

  const preloadPages: number[] = useMemo(() => {
    if (isContinuous) return [];
    const out: number[] = [];
    for (let i = 1; i <= settings.preload; i++) {
      const n = currentPage + i;
      if (n < pageCount) out.push(n);
    }
    return out;
  }, [isContinuous, currentPage, settings.preload, pageCount]);

  const previewChapter = useMemo(
    () => previewOrder?.find((c) => c.index === previewChapterIndex) ?? null,
    [previewOrder, previewChapterIndex],
  );
  const chapterLabel = isPreview
    ? (previewChapter?.name || `Chapter ${previewChapterIndex + 1}`)
    : chapter ? (chapter.name || `Chapter ${chapter.number}`) : "";

  return (
    <div className="fixed inset-0 z-50 select-none" style={{ background: BG[settings.background] }}>
      {/* ── Content ── */}
      {error ? (
        <div className="flex h-full flex-col items-center justify-center gap-4 text-white/80">
          <p>{error}</p>
          <Button variant="secondary" onClick={() => router.back()}>Go back</Button>
        </div>
      ) : loading ? (
        <div className="flex h-full flex-col items-center justify-center gap-4 text-white/70">
          <div className="h-8 w-8 animate-spin rounded-full border-2 border-white/30 border-t-white/90" />
          {openingLabel ? (
            <div className="text-center">
              <div className="text-xs uppercase tracking-[0.15em] text-white/40">Opening</div>
              <div className="mt-1 text-sm font-medium text-white/90">{openingLabel}</div>
            </div>
          ) : (
            <div className="text-sm text-white/50">Loading chapter…</div>
          )}
        </div>
      ) : isContinuous ? (
        <div ref={scrollRef} className="h-full overflow-y-auto" onClick={handleTap}>
          <div className="mx-auto flex flex-col items-center" style={{ ...containerWidthStyle, rowGap: gap }}>
            {pagesToRender.map((i) => (
              <div
                key={i}
                data-page={i}
                ref={(el) => { if (el) pageRefs.current.set(i, el); else pageRefs.current.delete(i); }}
                className="w-full"
                // Reserve each page's real height up front. Without this, lazy
                // images are zero-height until they load, every page stacks at
                // the top, and page tracking (and therefore progress) is
                // nonsense. Library chapters ship exact dimensions from the
                // server; preview falls back to a sane placeholder height.
                style={pageAspect(i)}
              >
                {/* eslint-disable-next-line @next/next/no-img-element */}
                <img
                  src={pageUrl(i)}
                  alt={`Page ${i + 1}`}
                  loading="lazy"
                  className="block w-full h-auto"
                  onLoad={(e) => onImageLoaded(i, e.currentTarget.naturalWidth, e.currentTarget.naturalHeight)}
                />
              </div>
            ))}
            <EndOfChapter
              chapterLabel={chapterLabel}
              hasNext={hasChapter(1)}
              hasPrev={hasChapter(-1)}
              onNext={() => goToChapter(1)}
              onPrev={() => goToChapter(-1)}
              onExit={() => router.back()}
            />
          </div>
        </div>
      ) : (
        <div className="flex h-full items-center justify-center overflow-hidden" onClick={handleTap}>
          <div className={`flex h-full items-center justify-center ${isRtl ? "flex-row-reverse" : ""}`}>
            {pagesToRender.map((i) => (
              // eslint-disable-next-line @next/next/no-img-element
              <img
                key={i}
                src={pageUrl(i)}
                alt={`Page ${i + 1}`}
                className={`${fitClass} max-h-screen object-contain`}
                style={resolvedMode === "double" ? { maxWidth: "50vw" } : { maxWidth: "100vw" }}
                onLoad={(e) => onImageLoaded(i, e.currentTarget.naturalWidth, e.currentTarget.naturalHeight)}
              />
            ))}
          </div>
          {/* Preload upcoming pages invisibly */}
          <div className="hidden">
            {preloadPages.map((i) => (
              // eslint-disable-next-line @next/next/no-img-element
              <img key={i} src={pageUrl(i)} alt="" />
            ))}
          </div>

          {/* Last page — offer the next chapter explicitly instead of a dead end */}
          {currentPage + step >= pageCount && hasChapter(1) && (
            <button
              onClick={(e) => { e.stopPropagation(); goToChapter(1); }}
              className="absolute bottom-16 left-1/2 -translate-x-1/2 inline-flex items-center gap-2 rounded-full bg-white/90 px-4 py-2 text-sm font-medium text-black shadow-lg transition-colors hover:bg-white"
            >
              Next chapter
              <ChevronRight className="h-4 w-4" />
            </button>
          )}
        </div>
      )}

      {/* Brief confirmation that a new chapter opened */}
      {arrivedLabel && !loading && (
        <div className="pointer-events-none absolute left-1/2 top-16 z-10 -translate-x-1/2 rounded-full bg-black/80 px-4 py-1.5 text-sm text-white shadow-lg backdrop-blur">
          {arrivedLabel}
        </div>
      )}

      {/* ── Top chrome ── */}
      {chromeVisible && !loading && (
        <div className="absolute inset-x-0 top-0 flex items-center gap-2 bg-black/70 px-3 py-2 text-white backdrop-blur">
          <button onClick={() => router.back()} className="rounded p-1.5 hover:bg-white/10" title="Back">
            <ArrowLeft className="h-5 w-5" />
          </button>
          <div className="min-w-0 flex-1">
            <div className="truncate text-sm font-medium">{chapters?.title ?? previewTitle}</div>
            <div className="truncate text-xs text-white/60">{chapterLabel}{isPreview ? " · Preview" : ""}</div>
          </div>
          {isPreview && (
            <button onClick={() => void downloadPreviewChapter()} className="rounded p-1.5 hover:bg-white/10" title="Download this chapter (requires the series in your library)">
              <Download className="h-5 w-5" />
            </button>
          )}
          {!isPreview && chapter && (
            <button onClick={() => void toggleBookmark()} className="rounded p-1.5 hover:bg-white/10" title={chapter.bookmarked ? "Remove bookmark" : "Bookmark chapter"}>
              <Bookmark className={`h-5 w-5 ${chapter.bookmarked ? "fill-current text-pink-400" : ""}`} />
            </button>
          )}
          <button onClick={() => setSettingsOpen((v) => !v)} className="rounded p-1.5 hover:bg-white/10" title="Reader settings">
            <Settings2 className="h-5 w-5" />
          </button>
        </div>
      )}

      {/* ── Bottom chrome ── */}
      {chromeVisible && !loading && !error && pageCount > 0 && (
        <div className="absolute inset-x-0 bottom-0 flex items-center gap-3 bg-black/70 px-4 py-2.5 text-white backdrop-blur">
          <button onClick={() => goToChapter(-1)} className="rounded p-1.5 hover:bg-white/10" title="Previous chapter">
            <ChevronLeft className="h-5 w-5" />
          </button>
          <div className="flex-1" dir={isRtl && !isContinuous ? "rtl" : "ltr"}>
            <Slider
              value={[currentPage]}
              min={0}
              max={Math.max(0, pageCount - 1)}
              step={1}
              onValueChange={(v) => {
                const target = v[0] ?? 0;
                setCurrentPage(target);
                if (isContinuous) pageRefs.current.get(target)?.scrollIntoView();
              }}
            />
          </div>
          {settings.showPageNumber && (
            <span className="shrink-0 text-xs tabular-nums text-white/70">
              {Math.min(currentPage + 1, pageCount)} / {pageCount}
            </span>
          )}
          <button onClick={() => goToChapter(1)} className="rounded p-1.5 hover:bg-white/10" title="Next chapter">
            <ChevronRight className="h-5 w-5" />
          </button>
        </div>
      )}

      {/* ── Settings panel ── */}
      {settingsOpen && (
        <div className="absolute right-0 top-12 bottom-0 w-80 max-w-[90vw] overflow-y-auto bg-zinc-900/95 p-4 text-white backdrop-blur border-l border-white/10">
          <div className="mb-3 flex items-center justify-between">
            <h2 className="text-sm font-semibold">Reader Settings</h2>
            <button onClick={() => setSettingsOpen(false)} className="rounded p-1 hover:bg-white/10"><X className="h-4 w-4" /></button>
          </div>
          <div className="space-y-4 text-sm">
            <div className="space-y-1.5">
              <Label>Reading mode {settings.mode === "auto" && !seriesModeOverride ? `(auto → ${resolvedMode})` : ""}</Label>
              <Select
                value={seriesModeOverride ?? settings.mode}
                onValueChange={(v) => {
                  const mode = v as ReaderMode;
                  if (!isPreview && seriesId) {
                    // Per-series override; "auto" clears it.
                    setSeriesModeOverride(mode === "auto" ? null : mode);
                    try {
                      if (mode === "auto") localStorage.removeItem(seriesModeKey(seriesId));
                      else localStorage.setItem(seriesModeKey(seriesId), mode);
                    } catch { /* ignore */ }
                  } else {
                    persistSettings({ ...settings, mode });
                  }
                }}
              >
                <SelectTrigger className="bg-zinc-800 border-white/10"><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="auto">Auto (smart detect)</SelectItem>
                  <SelectItem value="paged">Paged — left to right</SelectItem>
                  <SelectItem value="paged-rtl">Paged — right to left</SelectItem>
                  <SelectItem value="double">Double page</SelectItem>
                  <SelectItem value="webtoon">Webtoon (no gaps)</SelectItem>
                  <SelectItem value="longstrip">Long strip (width-matched)</SelectItem>
                  <SelectItem value="vertical">Vertical (with gaps)</SelectItem>
                </SelectContent>
              </Select>
              {!isPreview && <p className="text-[11px] text-white/50">Choice is remembered per series; “Auto” follows the chapter’s detected layout.</p>}
            </div>

            {!isContinuous && (
              <div className="space-y-1.5">
                <Label>Page fit</Label>
                <Select value={settings.fit} onValueChange={(v) => persistSettings({ ...settings, fit: v as FitMode })}>
                  <SelectTrigger className="bg-zinc-800 border-white/10"><SelectValue /></SelectTrigger>
                  <SelectContent>
                    <SelectItem value="width">Fit width</SelectItem>
                    <SelectItem value="height">Fit height</SelectItem>
                    <SelectItem value="original">Original size</SelectItem>
                  </SelectContent>
                </Select>
              </div>
            )}

            {isContinuous && (
              <div className="space-y-1.5">
                <Label>Page width — {settings.maxWidthPct}% of screen</Label>
                <Slider
                  value={[settings.maxWidthPct]}
                  min={20} max={100} step={5}
                  onValueChange={(v) => persistSettings({ ...settings, maxWidthPct: v[0] ?? 60 })}
                />
                <p className="text-[11px] text-white/50">
                  Auto-resize: every page is scaled to this same width, so mixed-size pages line up.
                </p>
              </div>
            )}

            {resolvedMode === "vertical" && (
              <div className="space-y-1.5">
                <Label>Gap between pages — {settings.gapPx}px</Label>
                <Slider value={[settings.gapPx]} min={0} max={48} step={4}
                  onValueChange={(v) => persistSettings({ ...settings, gapPx: v[0] ?? 12 })} />
              </div>
            )}

            <div className="space-y-1.5">
              <Label>Background</Label>
              <Select value={settings.background} onValueChange={(v) => persistSettings({ ...settings, background: v as ReaderSettings["background"] })}>
                <SelectTrigger className="bg-zinc-800 border-white/10"><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="black">Black</SelectItem>
                  <SelectItem value="gray">Dark gray</SelectItem>
                  <SelectItem value="white">White</SelectItem>
                </SelectContent>
              </Select>
            </div>

            <div className="space-y-1.5">
              <Label>Preload pages — {settings.preload}</Label>
              <Slider value={[settings.preload]} min={0} max={10} step={1}
                onValueChange={(v) => persistSettings({ ...settings, preload: v[0] ?? 4 })} />
            </div>

            <ToggleRow label="Tap zones (left/right to turn pages)" checked={settings.tapNavigation}
              onChange={(v) => persistSettings({ ...settings, tapNavigation: v })} />
            <ToggleRow label="Show page number" checked={settings.showPageNumber}
              onChange={(v) => persistSettings({ ...settings, showPageNumber: v })} />
            <ToggleRow label="Auto-advance to next chapter" checked={settings.autoAdvance}
              onChange={(v) => persistSettings({ ...settings, autoAdvance: v })} />
            {!isPreview && (
              <ToggleRow label="Mark read on last page" checked={settings.autoMarkRead}
                onChange={(v) => persistSettings({ ...settings, autoMarkRead: v })} />
            )}

            {!isPreview && chapter && (
              <div className="border-t border-white/10 pt-3">
                <Button
                  variant="secondary" size="sm" className="w-full"
                  onClick={() => {
                    if (!seriesId || chapterNumber == null) return;
                    void readerService.markChapters(seriesId, [chapterNumber], !chapter.isCompleted).then(() => {
                      setChapters((prev) => prev && {
                        ...prev,
                        chapters: prev.chapters.map((c) => c.number === chapterNumber ? { ...c, isCompleted: !chapter.isCompleted, progress: chapter.isCompleted ? 0 : 1 } : c),
                      });
                    });
                  }}
                >
                  {chapter.isCompleted ? "Mark chapter unread" : "Mark chapter read"}
                </Button>
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

/**
 * Footer of a continuous (webtoon / long-strip / vertical) chapter: makes the
 * next chapter a one-tap action instead of leaving the reader at a dead end.
 */
function EndOfChapter({
  chapterLabel, hasNext, hasPrev, onNext, onPrev, onExit,
}: {
  chapterLabel: string;
  hasNext: boolean;
  hasPrev: boolean;
  onNext: () => void;
  onPrev: () => void;
  onExit: () => void;
}) {
  return (
    <div
      className="flex w-full flex-col items-center gap-4 py-14 text-center"
      onClick={(e) => e.stopPropagation()}
    >
      <div>
        <div className="text-xs uppercase tracking-[0.15em] text-white/35">End of</div>
        <div className="mt-1 text-sm font-medium text-white/80">{chapterLabel}</div>
      </div>
      <div className="flex flex-wrap items-center justify-center gap-2">
        {hasPrev && (
          <Button variant="secondary" onClick={onPrev} className="gap-1.5">
            <ChevronLeft className="h-4 w-4" />
            Previous
          </Button>
        )}
        {hasNext ? (
          <Button onClick={onNext} className="gap-1.5">
            Next chapter
            <ChevronRight className="h-4 w-4" />
          </Button>
        ) : (
          <Button variant="secondary" onClick={onExit}>
            Back to series
          </Button>
        )}
      </div>
      {hasNext && <p className="text-xs text-white/35">or press → </p>}
    </div>
  );
}

function ToggleRow({ label, checked, onChange }: { label: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <div className="flex items-center justify-between gap-3">
      <Label className="text-sm font-normal">{label}</Label>
      <Switch checked={checked} onCheckedChange={onChange} />
    </div>
  );
}

export default function ReaderPage() {
  return (
    <Suspense fallback={<div className="fixed inset-0 bg-black" />}>
      <ReaderInner />
    </Suspense>
  );
}
