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
 *
 * Continuous modes (webtoon / longstrip / vertical) scroll infinitely across
 * chapter boundaries: as you near the bottom the next chapter's pages are
 * appended inline so reading never dead-ends on a chapter break. Navigation is
 * by tap/click zones — sides turn pages in paged mode, and an adjustable
 * click-to-scroll advances a continuous strip by a fixed fraction of the screen.
 */

import React, { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, Suspense } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import {
  ArrowLeft, Bookmark, Check, ChevronLeft, ChevronRight, Download, ExternalLink, List, Loader2, Lock, Search, Settings2, Trash2, X,
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
import { type ReaderChapters, type ReaderChapter, type ReaderChapterInfo, type ReaderPageDims, type PreviewChapter } from "@/lib/api/types";

type ReaderMode = "auto" | "paged" | "paged-rtl" | "double" | "webtoon" | "longstrip" | "vertical";
type FitMode = "width" | "height" | "original";

/**
 * One chapter laid out in the continuous scroller. The first segment is always
 * the chapter the reader opened on; later ones are appended as the reader scrolls
 * past a chapter boundary (infinite scroll).
 */
interface Segment {
  key: string;
  chapterNumber: number | null;    // library reading
  previewIndex: number | null;     // preview reading
  name: string;
  pageCount: number;
  streaming: boolean;              // library chapter not downloaded — pages come live from the source
  filename: string | null;         // archive filename when downloaded
  pages: ReaderPageDims[] | null;  // exact per-page dims (downloaded library chapters)
}

// Rebindable reader actions. `nextPage`/`prevPage` navigate WITHIN a chapter
// (page turn when paged, scroll step when continuous) and never skip chapters —
// chapter skipping is its own pair of actions.
type HotkeyAction =
  | "nextPage" | "prevPage"
  | "scrollDown" | "scrollUp"
  | "nextChapter" | "prevChapter"
  | "firstPage" | "lastPage"
  | "toggleChrome" | "toggleChapters" | "toggleSettings"
  | "bookmark" | "exit";

// Display order + human labels for the in-reader hotkey editor.
const HOTKEY_ACTIONS: { action: HotkeyAction; label: string }[] = [
  { action: "nextPage", label: "Next page / scroll forward" },
  { action: "prevPage", label: "Previous page / scroll back" },
  { action: "scrollDown", label: "Scroll down" },
  { action: "scrollUp", label: "Scroll up" },
  { action: "nextChapter", label: "Next chapter" },
  { action: "prevChapter", label: "Previous chapter" },
  { action: "firstPage", label: "Jump to first page" },
  { action: "lastPage", label: "Jump to last page" },
  { action: "toggleChrome", label: "Show / hide controls" },
  { action: "toggleChapters", label: "Chapter list" },
  { action: "toggleSettings", label: "Settings panel" },
  { action: "bookmark", label: "Bookmark chapter" },
  { action: "exit", label: "Exit reader" },
];

type Hotkeys = Record<HotkeyAction, string>;

const DEFAULT_HOTKEYS: Hotkeys = {
  nextPage: "ArrowRight",
  prevPage: "ArrowLeft",
  scrollDown: "ArrowDown",
  scrollUp: "ArrowUp",
  nextChapter: "]",
  prevChapter: "[",
  firstPage: "Home",
  lastPage: "End",
  toggleChrome: "Escape",
  toggleChapters: "l",
  toggleSettings: "s",
  bookmark: "b",
  exit: "c",
};

// Normalize a KeyboardEvent to the token we store/compare. Space is stored as
// "Space" (its e.key is a literal " ", which is invisible in the UI).
function eventKeyToken(e: KeyboardEvent): string {
  if (e.key === " " || e.code === "Space") return "Space";
  return e.key;
}

// Pretty-print a stored key token for the editor / hints.
function keyLabel(token: string): string {
  switch (token) {
    case "ArrowRight": return "→";
    case "ArrowLeft": return "←";
    case "ArrowUp": return "↑";
    case "ArrowDown": return "↓";
    case "Space": return "Space";
    case "Escape": return "Esc";
    case "": return "—";
    default: return token.length === 1 ? token.toUpperCase() : token;
  }
}

interface ReaderSettings {
  mode: ReaderMode;
  fit: FitMode;
  maxWidthPct: number;     // % of viewport width cap in continuous modes
  background: "black" | "gray" | "white";
  preload: number;
  gapPx: number;           // vertical mode gap
  showPageNumber: boolean;
  tapNavigation: boolean;
  tapAdvancePct: number;   // continuous click-to-scroll step, as % of viewport height
  infiniteScroll: boolean; // continuous: append the next chapter at the bottom
  chapterTransition: boolean; // show a "finished / up next" screen between chapters (paged: its own page)
  autoMarkRead: boolean;
  autoClearCache: boolean;  // clear the streamed-page cache when leaving the reader
  hotkeys: Hotkeys;
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
  tapAdvancePct: 80,
  infiniteScroll: true,
  chapterTransition: true,
  autoMarkRead: true,
  autoClearCache: true,
  hotkeys: DEFAULT_HOTKEYS,
};

const SETTINGS_KEY = "renzo_reader_settings";
// Continuous scroll keeps at most this many chapters on each side of the active
// one loaded; the rest are pruned (and their page cache freed) as you scroll.
const CHAPTER_WINDOW = 2;
const seriesModeKey = (id: string) => `renzo_reader_mode_${id}`;

function loadSettings(): ReaderSettings {
  if (typeof window === "undefined") return DEFAULT_SETTINGS;
  try {
    const raw = localStorage.getItem(SETTINGS_KEY);
    if (!raw) return DEFAULT_SETTINGS;
    const parsed = JSON.parse(raw) as Partial<ReaderSettings>;
    // Merge hotkeys key-by-key so a saved partial map (or an older build with no
    // hotkeys at all) still gets defaults for any action it's missing.
    return {
      ...DEFAULT_SETTINGS,
      ...parsed,
      hotkeys: { ...DEFAULT_HOTKEYS, ...(parsed.hotkeys ?? {}) },
    };
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
  const [currentPage, setCurrentPage] = useState(0); // 0-based, within the active segment
  // Chapters appended below / prepended above the opening chapter for infinite
  // continuous scroll in both directions.
  const [appended, setAppended] = useState<Segment[]>([]);
  const [prepended, setPrepended] = useState<Segment[]>([]);
  // Which segment ([...prepended, primary, ...appended]) is currently on screen.
  const [activeSegIndex, setActiveSegIndex] = useState(0);
  // Sliding window: continuous scroll keeps at most CHAPTER_WINDOW chapters on
  // either side of the active one, pruning (and so freeing the page cache of)
  // chapters that scroll out of range. Once the opening chapter itself scrolls
  // out of the window it's dropped too (primaryHidden) so the window can move
  // freely; the list stays contiguous because that only happens when one side is
  // fully pruned. pruneAnchorRef re-anchors the viewport after a top-side drop.
  const [primaryHidden, setPrimaryHidden] = useState(false);
  const [pruneNonce, setPruneNonce] = useState(0);
  const pruneAnchorRef = useRef<{ gi: number; viewportTop: number } | null>(null);
  const [chromeVisible, setChromeVisible] = useState(true);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [chaptersOpen, setChaptersOpen] = useState(false);
  const [clearingCache, setClearingCache] = useState(false);
  // When set, the next keypress rebinds this action instead of running it.
  const [capturingAction, setCapturingAction] = useState<HotkeyAction | null>(null);
  const capturingRef = useRef<HotkeyAction | null>(null);
  useEffect(() => { capturingRef.current = capturingAction; }, [capturingAction]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  // Bumped to force the loader to re-run (e.g. after a locked chapter unlocks).
  const [reloadNonce, setReloadNonce] = useState(0);
  // True when the opening library chapter isn't downloaded and is being streamed
  // live from the source (read-without-downloading / in-progress downloads).
  const [streaming, setStreaming] = useState(false);
  // Set when a stream attempt reveals the chapter is paid/locked (the source
  // withheld the pages) even though the title carried no lock marker.
  const [streamLocked, setStreamLocked] = useState(false);
  // Auto-clear the server's streamed-page cache on reader exit. Only worth doing
  // if we actually streamed/previewed anything (downloaded library pages don't
  // touch that cache). Refs so the unmount cleanup reads the latest values
  // without re-running.
  const autoClearCacheRef = useRef(true);
  const usedStreamRef = useRef(false);
  // Name of the chapter being switched to — drives the "opening…" overlay so a
  // chapter change is never a silent blank screen, and the toast shown once it lands.
  const [openingLabel, setOpeningLabel] = useState<string | null>(null);
  const [arrivedLabel, setArrivedLabel] = useState<string | null>(null);
  // Client-side strip detection for preview mode (naturalWidth/Height as images load)
  const [detectedMode, setDetectedMode] = useState<"webtoon" | "longstrip" | "paged" | null>(null);
  const loadedDimsRef = useRef<Map<number, { w: number; h: number }>>(new Map());
  const scrollRef = useRef<HTMLDivElement>(null);
  const pageRefs = useRef<Map<number, HTMLDivElement>>(new Map());
  const progressSentRef = useRef<{ key: string; page: number; at: number }>({ key: "", page: -1, at: 0 });
  // Progress reporting is "armed" a moment after a chapter loads, so the position
  // churn during initial layout/resume doesn't get written as real reading.
  const progressArmedAtRef = useRef(0);
  const markedReadRef = useRef<Set<number>>(new Set());
  const appendLockRef = useRef(false);
  // Set once infinite scroll can't append further (end of series, or the next
  // chapter is locked) so it stops retrying on every scroll frame.
  const appendStoppedRef = useRef(false);
  const prependLockRef = useRef(false);
  const prependStoppedRef = useRef(false);
  // Drives the visible "Loading next/previous chapter…" boundary indicators so an
  // adjacent chapter is only pulled deliberately (near the boundary) and shown —
  // not eagerly/silently, which was hammering the source.
  const [appending, setAppending] = useState(false);
  const [prepending, setPrepending] = useState(false);
  // scrollHeight captured just before a prepend, so the layout effect can add the
  // inserted height back to scrollTop and keep the viewport visually anchored.
  const prependAdjustRef = useRef<number | null>(null);
  // Last scroll position, so upward scrolls (which should prepend the previous
  // chapter) are distinguished from the initial at-top state (which should not).
  const lastScrollTopRef = useRef(0);
  // The page a freshly-opened chapter should land on: 0 = top (a new chapter
  // always starts at the top, never inheriting the previous chapter's scroll),
  // or the resume page when re-opening a partially-read chapter.
  const resumePageRef = useRef(0);

  const persistSettings = useCallback((next: ReaderSettings) => {
    setSettings(next);
    try { localStorage.setItem(SETTINGS_KEY, JSON.stringify(next)); } catch { /* private mode */ }
  }, []);

  // ── Current chapter + navigable set ───────────────────────────────────
  const chapter = useMemo(() => {
    if (!chapters || chapterNumber == null) return null;
    return chapters.chapters.find((c) => c.number === chapterNumber) ?? null;
  }, [chapters, chapterNumber]);

  // Every chapter with a number is navigable now: downloaded ones read from the
  // archive, and not-yet-downloaded ones stream live from the source.
  const readableChapters = useMemo(
    () => (chapters?.chapters ?? []).filter((c) => c.number != null),
    [chapters],
  );

  const previewChapter = useMemo(
    () => previewOrder?.find((c) => c.index === previewChapterIndex) ?? null,
    [previewOrder, previewChapterIndex],
  );
  const chapterLabel = isPreview
    ? (previewChapter?.name || `Chapter ${previewChapterIndex + 1}`)
    : chapter ? (chapter.name || `Chapter ${chapter.number}`) : "";

  // ── Segment builders (used for the appended chapters in infinite scroll) ──
  const buildLibrarySeg = useCallback(async (num: number): Promise<Segment | null> => {
    if (!seriesId || !chapters) return null;
    const target = chapters.chapters.find((c) => c.number === num);
    if (!target || target.locked) return null; // don't roll a locked chapter into the scroll
    const name = target.name || `Chapter ${num}`;
    if (target.filename) {
      const ci = await readerService.getChapterInfo(seriesId, target.filename);
      return { key: `lib:${num}`, chapterNumber: num, previewIndex: null, name, pageCount: ci.pageCount, streaming: false, filename: target.filename, pages: ci.pages };
    }
    const sp = await readerService.streamPages(seriesId, num);
    return { key: `lib:${num}`, chapterNumber: num, previewIndex: null, name, pageCount: sp.pageCount, streaming: true, filename: null, pages: null };
  }, [seriesId, chapters]);

  const buildPreviewSeg = useCallback(async (idx: number): Promise<Segment | null> => {
    if (!mihonId) return null;
    const meta = previewOrder?.find((c) => c.index === idx);
    const sp = await readerService.getPreviewPages(mihonId, idx);
    return { key: `pv:${idx}`, chapterNumber: null, previewIndex: idx, name: meta?.name || `Chapter ${idx + 1}`, pageCount: sp.pageCount, streaming: false, filename: null, pages: null };
  }, [mihonId, previewOrder]);

  // Keep the auto-clear ref current, and remember once we've streamed/previewed.
  useEffect(() => { autoClearCacheRef.current = settings.autoClearCache; }, [settings.autoClearCache]);
  useEffect(() => { if (isPreview || streaming) usedStreamRef.current = true; }, [isPreview, streaming]);
  // On reader exit (unmount) clear the server's streamed-page cache — but only if
  // we actually streamed/previewed pages (downloaded library reads don't use it).
  useEffect(() => () => {
    if (autoClearCacheRef.current && usedStreamRef.current) {
      readerService.clearStreamCache().catch(() => {});
    }
  }, []);

  // ── Data loading (the opening chapter) ────────────────────────────────
  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      setLoading(true);
      setError(null);
      setCurrentPage(0);
      resumePageRef.current = 0;
      setInfo(null);
      setStreaming(false);
      setStreamLocked(false);
      setDetectedMode(null);
      setAppended([]);
      setPrepended([]);
      setPrimaryHidden(false);
      pruneAnchorRef.current = null;
      setActiveSegIndex(0);
      appendStoppedRef.current = false;
      prependStoppedRef.current = false;
      prependAdjustRef.current = null;
      progressArmedAtRef.current = Date.now() + 1200;
      loadedDimsRef.current.clear();
      markedReadRef.current = new Set();
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
          if (!target) {
            setError("Chapter not found.");
            setLoading(false);
            return;
          }
          if (target.locked) {
            // Paid/locked — show the purchase screen instead of streaming pages.
            try {
              setSeriesModeOverride((localStorage.getItem(seriesModeKey(seriesId)) as ReaderMode) || null);
            } catch { /* ignore */ }
            setLoading(false);
            return;
          }
          if (!target.filename) {
            // Not downloaded yet — stream it live from the source so it can be
            // read right away (e.g. while its download is still in the queue).
            setStreaming(true);
            const sp = await readerService.streamPages(seriesId, chapterNumber);
            if (cancelled) return;
            if (sp.locked || sp.pageCount <= 0) {
              // Source withheld the pages — a paid/locked chapter. Show the buy
              // screen (and the 3s poll picks it up once purchased / free).
              setStreaming(false);
              setStreamLocked(true);
              setLoading(false);
              return;
            }
            setPageCount(sp.pageCount);
            if (target.progress > 0 && target.progress < 1 && sp.pageCount > 0) {
              const resume = Math.min(sp.pageCount - 1, Math.floor(target.progress * sp.pageCount));
              setCurrentPage(resume);
              resumePageRef.current = resume;
            }
          } else {
            const ci = await readerService.getChapterInfo(seriesId, target.filename);
            if (cancelled) return;
            setInfo(ci);
            setPageCount(ci.pageCount);
            // Resume where the user left off (not at 100%)
            if (target.progress > 0 && target.progress < 1 && ci.pageCount > 0) {
              const resume = Math.min(ci.pageCount - 1, Math.floor(target.progress * ci.pageCount));
              setCurrentPage(resume);
              resumePageRef.current = resume;
            }
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
  }, [isPreview, mihonId, seriesId, chapterNumber, previewChapterIndex, reloadNonce]);

  // Locked-chapter unlock detection, event-driven: the purchase happens in the
  // tab the Buy button opened, so the moment that matters is when THIS tab
  // regains focus — check immediately then. A manual "Check again" button gives
  // certainty, and a lazy 60s heartbeat covers purchases made on another device
  // and chapters turning free with time. Checks never overlap and are spaced at
  // least a few seconds apart, so the source isn't hammered.
  const unlockCheckStateRef = useRef({ inFlight: false, last: 0 });
  const manualUnlockCheckRef = useRef<(() => void) | null>(null);
  const [unlockChecking, setUnlockChecking] = useState(false);
  useEffect(() => {
    const showingLocked = !!chapter?.locked || streamLocked;
    if (isPreview || !seriesId || chapterNumber == null || !showingLocked) return;
    let cancelled = false;
    let timer: number | undefined;

    const runCheck = async (minGapMs: number) => {
      const st = unlockCheckStateRef.current;
      const now = Date.now();
      if (st.inFlight || now - st.last < minGapMs) return;
      st.inFlight = true;
      st.last = now;
      setUnlockChecking(true);
      try {
        // Force a fresh source fetch (bypass the cached empty page list).
        const sp = await readerService.streamPages(seriesId, chapterNumber, true);
        if (cancelled) return;
        if (!sp.locked && sp.pageCount > 0) {
          setLoading(true);
          setStreamLocked(false);
          setChapters((prev) => prev && {
            ...prev,
            chapters: prev.chapters.map((c) => c.number === chapterNumber ? { ...c, locked: false } : c),
          });
          setReloadNonce((n) => n + 1);
        }
      } catch { /* still locked */ }
      finally {
        st.inFlight = false;
        if (!cancelled) setUnlockChecking(false);
      }
    };

    manualUnlockCheckRef.current = () => void runCheck(1000);

    // Returning to this tab after buying → check right away (small gap guard
    // absorbs the focus+visibilitychange double-fire).
    const onReturn = () => {
      if (document.visibilityState === "visible") void runCheck(4000);
    };
    window.addEventListener("focus", onReturn);
    document.addEventListener("visibilitychange", onReturn);

    // One early check (the chapter may already be owned), then a 60s heartbeat.
    const loop = async () => {
      if (cancelled) return;
      await runCheck(0);
      if (!cancelled) timer = window.setTimeout(() => void loop(), 60000);
    };
    timer = window.setTimeout(() => void loop(), 3000);

    return () => {
      cancelled = true;
      if (timer) window.clearTimeout(timer);
      window.removeEventListener("focus", onReturn);
      document.removeEventListener("visibilitychange", onReturn);
      manualUnlockCheckRef.current = null;
    };
  }, [isPreview, seriesId, chapterNumber, chapter?.locked, streamLocked]);

  // ── Mode resolution ───────────────────────────────────────────────────
  const resolvedMode: Exclude<ReaderMode, "auto"> = useMemo(() => {
    const chosen = seriesModeOverride ?? settings.mode;
    if (chosen !== "auto") return chosen;
    // Library: server-computed suggestion from the ACTUAL archive page
    // dimensions. This measurement is authoritative — a series whose pages are
    // discrete, similar-sized pages reads as paged even if its type label says
    // manhwa/manhua. (The type label is only a fallback below, when nothing has
    // been measured yet.)
    if (info) {
      if (info.suggestedMode === "webtoon") return "webtoon";
      if (info.suggestedMode === "longstrip") return "longstrip";
      // Measured as paged: honor it. Japanese manga reads right-to-left.
      const type = (chapters?.type ?? "").toLowerCase();
      if (type.includes("manga") && !type.includes("manhwa") && !type.includes("manhua"))
        return "paged-rtl";
      return "paged";
    }
    // Preview/stream: decided from image natural sizes as they load.
    if (detectedMode === "webtoon") return "webtoon";
    if (detectedMode === "longstrip") return "longstrip";
    // A measured "paged" result is authoritative here too — don't let the type
    // label below flip a discrete-page series back to a scroll.
    const st = (chapters?.type ?? "").toLowerCase();
    if (detectedMode === "paged")
      return st.includes("manga") && !st.includes("manhwa") && !st.includes("manhua") ? "paged-rtl" : "paged";
    // Nothing measured yet: fall back to the type label so a streamed
    // manhwa/manhua/webtoon starts scrolling immediately (a later measurement
    // can still correct it).
    if (streaming && (st.includes("manhwa") || st.includes("manhua") || st.includes("webtoon")))
      return "webtoon";
    return "paged";
  }, [settings.mode, seriesModeOverride, info, detectedMode, chapters, streaming]);

  const isContinuous = resolvedMode === "webtoon" || resolvedMode === "longstrip" || resolvedMode === "vertical";
  const isRtl = resolvedMode === "paged-rtl";

  // ── Segments (opening chapter + appended chapters) ────────────────────
  const primarySeg: Segment = useMemo(() => ({
    key: "primary",
    chapterNumber: isPreview ? null : chapterNumber,
    previewIndex: isPreview ? previewChapterIndex : null,
    name: chapterLabel,
    pageCount,
    streaming,
    filename: chapter?.filename ?? null,
    pages: info?.pages ?? null,
  }), [isPreview, chapterNumber, previewChapterIndex, chapterLabel, pageCount, streaming, chapter, info]);

  const segments = useMemo(
    () => (primaryHidden ? [...prepended, ...appended] : [...prepended, primarySeg, ...appended]),
    [prepended, primarySeg, appended, primaryHidden],
  );
  const segOffsets = useMemo(() => {
    const offs: number[] = [];
    let acc = 0;
    for (const s of segments) { offs.push(acc); acc += s.pageCount; }
    return offs;
  }, [segments]);
  const totalPages = useMemo(() => segments.reduce((a, s) => a + s.pageCount, 0), [segments]);

  const activeSeg = segments[activeSegIndex] ?? primarySeg;
  const activeChapterNumber = isContinuous ? activeSeg.chapterNumber : chapterNumber;
  const activeChapterObj = useMemo(
    () => (activeChapterNumber == null ? null : chapters?.chapters.find((c) => c.number === activeChapterNumber) ?? null),
    [chapters, activeChapterNumber],
  );
  // Page count / label the chrome (slider, counter, title) reflects.
  const activePageCount = isContinuous ? activeSeg.pageCount : pageCount;
  const activeLabel = isContinuous ? activeSeg.name : chapterLabel;

  // Global page index in view — used to keep only nearby images mounted so a long
  // infinite scroll can't pile up decoded bitmaps and choke the browser. Pages
  // outside the window render as empty (height-preserving) boxes and reload from
  // the server cache instantly when scrolled back to.
  const activeGi = (segOffsets[activeSegIndex] ?? 0) + currentPage;
  const IMAGE_WINDOW = 30;

  // Preview smart detection: after enough images report natural sizes, classify.
  const onImageLoaded = useCallback((gi: number, w: number, h: number) => {
    // Always record real dimensions so the page box can match the image exactly
    // (prevents gaps between pages in continuous mode).
    if (w > 0 && h > 0) loadedDimsRef.current.set(gi, { w, h });
    // Once webtoon/longstrip is detected, that's a confident, final verdict —
    // stop re-checking. But an early "paged" read is NOT final: some webtoons
    // open with a few normal-shaped establishing panels before the strip-cut
    // pages that would actually flag it, so a decision made from only the
    // first few loaded images can be wrong. Keep re-evaluating as more pages
    // load so it can still upgrade to webtoon/longstrip once real evidence
    // shows up, instead of staying stuck on paged for the whole chapter.
    if ((!isPreview && !streaming) || detectedMode === "webtoon" || detectedMode === "longstrip") return;
    const dims = [...loadedDimsRef.current.values()];
    // Mirrors the server-side rule (ReaderService): tall panels are native
    // webtoon artwork; short wide slivers are off-cuts left by slicing a long
    // strip into "pages", and enough of either means the chapter must be read
    // as a continuous, width-matched strip rather than page-by-page.
    if (dims.length >= Math.min(6, pageCount)) {
      const strips = dims.filter((d) => d.w > 0 && d.h / d.w >= 3).length;
      const slivers = dims.filter((d) => d.w > 0 && d.h / d.w <= 0.5).length;
      // ≥2.0 (not 1.6): ordinary paged comics include portrait pages up to ~1.9×,
      // so a lower bar misreads a paged series (e.g. "The Exiled Prince of
      // Auto-Crafting") as a webtoon. Mirrors the server's TallAspectThreshold.
      const tall = dims.filter((d) => d.w > 0 && d.h / d.w >= 2.0).length;
      if (strips * 2 >= dims.length) setDetectedMode("webtoon");
      else if (tall * 5 >= dims.length * 4) setDetectedMode("webtoon");
      // `tall` (≥2×), not `strips` (≥3×): a handful of 2–3× pages mixed into an
      // otherwise normal chapter used to fall through to paged, which squeezes
      // those pages down to fit a single screen. Sliver detection is untouched.
      else if (tall + slivers > 4) setDetectedMode("longstrip");
      else setDetectedMode("paged");
    }
  }, [isPreview, streaming, detectedMode, pageCount]);

  // ── Page URLs / aspect ────────────────────────────────────────────────
  const segPageUrl = useCallback((seg: Segment, i: number): string => {
    if (isPreview && mihonId && seg.previewIndex != null) return readerService.previewPageUrl(mihonId, seg.previewIndex, i);
    if (seg.streaming && seriesId && seg.chapterNumber != null) return readerService.streamPageUrl(seriesId, seg.chapterNumber, i);
    if (seriesId && seg.filename) return readerService.pageUrl(seriesId, seg.filename, i);
    return "";
  }, [isPreview, mihonId, seriesId]);

  /** Reserve a page box's real height up front so scroll tracking is correct before images load. */
  const segPageAspect = useCallback((seg: Segment, gi: number, i: number): React.CSSProperties => {
    // Once an image has loaded, match its exact decoded ratio — server-measured
    // dims can differ by a pixel and streamed pages have none, either of which
    // leaves a gap below the image in continuous mode.
    const loaded = loadedDimsRef.current.get(gi);
    if (loaded?.w && loaded?.h) return { aspectRatio: `${loaded.w} / ${loaded.h}` };
    const dims = seg.pages?.find((p) => p.index === i);
    if (dims?.width && dims?.height) return { aspectRatio: `${dims.width} / ${dims.height}` };
    return { minHeight: "70vh" };
  }, []);

  // Paged mode reads only the primary chapter.
  const pageUrl = useCallback((i: number): string => {
    if (isPreview && mihonId) return readerService.previewPageUrl(mihonId, previewChapterIndex, i);
    if (streaming && seriesId && chapterNumber != null) return readerService.streamPageUrl(seriesId, chapterNumber, i);
    if (seriesId && chapter?.filename) return readerService.pageUrl(seriesId, chapter.filename, i);
    return "";
  }, [isPreview, mihonId, previewChapterIndex, streaming, seriesId, chapterNumber, chapter]);

  // ── Progress reporting (library only) ─────────────────────────────────
  const reportProgressFor = useCallback((chapNum: number | null, filename: string | null, page0: number, total: number) => {
    if (isPreview || !seriesId || chapNum == null || total === 0) return;
    // Ignore the position churn right after a chapter opens (layout + resume jump).
    if (Date.now() < progressArmedAtRef.current) return;

    const page1 = page0 + 1;
    const key = String(chapNum);
    const last = progressSentRef.current;
    if (last.key === key && last.page === page1) return; // position unchanged

    const now = Date.now();
    const atEnd = page1 >= total;
    // Rate-limit writes within a chapter so scrolling (either direction) doesn't
    // spam; the end is always reported so completion isn't missed.
    if (last.key === key && !atEnd && now - last.at < 800) return;
    progressSentRef.current = { key, page: page1, at: now };

    const progress = Math.min(1, page1 / total);
    // Reflect the live position both ways; completion stays sticky, and a
    // completed chapter keeps its 100% (mirrors the server-side guard).
    setChapters((prev) => prev && {
      ...prev,
      chapters: prev.chapters.map((c) => c.number === chapNum
        ? { ...c, progress: c.isCompleted ? c.progress : progress, isCompleted: c.isCompleted || (settings.autoMarkRead && atEnd) }
        : c),
    });
    if (settings.autoMarkRead && atEnd) markedReadRef.current.add(chapNum);
    void readerService.setProgress(seriesId, chapNum, page1, total, filename ?? undefined)
      .catch(() => { /* transient */ });
  }, [isPreview, seriesId, settings.autoMarkRead]);

  // Paged: report against the (single) primary chapter. Skip the transition
  // screen (index === pageCount), which isn't a real page.
  useEffect(() => {
    if (isContinuous || currentPage >= pageCount) return;
    reportProgressFor(chapterNumber, chapter?.filename ?? null, currentPage, pageCount);
  }, [isContinuous, currentPage, chapterNumber, chapter, pageCount, reportProgressFor]);

  // Continuous: report against whichever appended chapter is on screen.
  useEffect(() => {
    if (!isContinuous) return;
    const seg = segments[activeSegIndex];
    if (!seg) return;
    reportProgressFor(seg.chapterNumber, seg.filename, currentPage, seg.pageCount);
  }, [isContinuous, activeSegIndex, currentPage, segments, reportProgressFor]);

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

  /** True when another chapter exists relative to a given segment's chapter. */
  const chapterExistsFrom = useCallback((seg: Segment, direction: 1 | -1): boolean => {
    if (isPreview) {
      if (!previewOrder) return false;
      const pos = previewOrder.findIndex((c) => c.index === seg.previewIndex);
      return pos >= 0 && !!previewOrder[pos + direction];
    }
    const idx = readableChapters.findIndex((c) => c.number === seg.chapterNumber);
    return idx >= 0 && !!readableChapters[idx + direction];
  }, [isPreview, previewOrder, readableChapters]);

  const hasChapter = useCallback((direction: 1 | -1): boolean => chapterExistsFrom(primarySeg, direction),
    [chapterExistsFrom, primarySeg]);

  /** Display name of the chapter that follows a given segment's chapter, if any. */
  const chapterNameAfter = useCallback((seg: Segment): string | null => {
    if (isPreview) {
      const pos = previewOrder?.findIndex((c) => c.index === seg.previewIndex) ?? -1;
      const nx = pos >= 0 ? previewOrder?.[pos + 1] : undefined;
      return nx ? (nx.name || `Chapter ${nx.index + 1}`) : null;
    }
    const idx = readableChapters.findIndex((c) => c.number === seg.chapterNumber);
    const nx = idx >= 0 ? readableChapters[idx + 1] : undefined;
    return nx ? (nx.name || `Chapter ${nx.number}`) : null;
  }, [isPreview, previewOrder, readableChapters]);

  // What the "current · up next" block reports, following the chapter on screen.
  const nextChapterName = useMemo(() => chapterNameAfter(activeSeg), [chapterNameAfter, activeSeg]);

  // Navigation is relative to the chapter actually ON SCREEN: with infinite
  // scroll the reader may be several chapters past the one it opened on, and
  // next/prev must move from there, not from the opening chapter.
  const goToChapter = useCallback((direction: 1 | -1) => {
    if (isPreview) {
      if (!previewOrder) return;
      const refIdx = isContinuous ? (activeSeg.previewIndex ?? previewChapterIndex) : previewChapterIndex;
      const pos = previewOrder.findIndex((c) => c.index === refIdx);
      const next = previewOrder[pos + direction];
      if (!next) {
        toast.info(direction > 0 ? "No next chapter." : "This is the first chapter.");
        return;
      }
      // Already loaded as a segment (infinite scroll) — just scroll to it.
      if (isContinuous) {
        const k = segments.findIndex((s) => s.previewIndex === next.index);
        if (k >= 0) { pageRefs.current.get(segOffsets[k] ?? 0)?.scrollIntoView(); return; }
      }
      setOpeningLabel(next.name || `Chapter ${next.index + 1}`);
      setPreviewChapterIndex(next.index);
      return;
    }
    if (!chapters || chapterNumber == null) return;
    const refNumber = isContinuous ? (activeSeg.chapterNumber ?? chapterNumber) : chapterNumber;
    const idx = readableChapters.findIndex((c) => c.number === refNumber);
    const next = readableChapters[idx + direction];
    if (!next) {
      toast.info(direction > 0 ? "That was the last chapter." : "This is the first chapter.");
      return;
    }
    // Already loaded as a segment (infinite scroll) — just scroll to it.
    if (isContinuous) {
      const k = segments.findIndex((s) => s.chapterNumber === next.number);
      if (k >= 0) { pageRefs.current.get(segOffsets[k] ?? 0)?.scrollIntoView(); return; }
    }
    setOpeningLabel(next.name || `Chapter ${next.number}`);
    setChapterNumber(next.number);
  }, [isPreview, previewOrder, previewChapterIndex, chapters, chapterNumber, readableChapters, isContinuous, activeSeg, segments, segOffsets]);

  const advance = useCallback((dir: 1 | -1) => {
    // Chapter switching must happen here, not inside the setState updater —
    // React may invoke updaters twice, which would skip a chapter.
    if (dir > 0) {
      if (currentPage >= pageCount) {
        // On the transition screen — one more click enters the next chapter.
        if (hasChapter(1)) goToChapter(1);
        return;
      }
      if (currentPage + step >= pageCount) {
        // Reached the last page. With the transition screen on, land on it (an
        // extra "page" at index pageCount); otherwise jump straight to the next.
        if (!settings.chapterTransition) { if (hasChapter(1)) goToChapter(1); }
        else setCurrentPage(pageCount);
        return;
      }
      setCurrentPage((p) => Math.min(pageCount - 1, p + step));
      return;
    }
    // Backward — from the transition screen this returns to the last page.
    setCurrentPage((p) => Math.max(0, Math.min(pageCount, p) - step));
  }, [currentPage, pageCount, step, settings.chapterTransition, hasChapter, goToChapter]);

  // Infinite scroll: append the chapter after the last-loaded segment. Kept in a
  // ref so the scroll listener always calls the latest closure without
  // re-subscribing on every state change.
  const maybeAppend = useCallback(() => {
    if (!settings.infiniteScroll || !isContinuous || appendLockRef.current || appendStoppedRef.current) return;
    const scroller = scrollRef.current;
    if (!scroller) return;
    // Defer to near the boundary (was ~1.5 screens) so the next chapter isn't
    // pulled from the source while the user is nowhere near the end.
    if (scroller.scrollHeight - (scroller.scrollTop + scroller.clientHeight) > scroller.clientHeight * 0.75) return;
    const last = segments[segments.length - 1];
    if (!last || !chapterExistsFrom(last, 1)) return;
    appendLockRef.current = true;
    setAppending(true);
    (async () => {
      try {
        let next: Segment | null = null;
        if (isPreview) {
          const pos = previewOrder?.findIndex((c) => c.index === last.previewIndex) ?? -1;
          const nx = pos >= 0 ? previewOrder?.[pos + 1] : undefined;
          if (nx) next = await buildPreviewSeg(nx.index);
        } else {
          const idx = readableChapters.findIndex((c) => c.number === last.chapterNumber);
          const nx = idx >= 0 ? readableChapters[idx + 1] : undefined;
          if (nx?.number != null) next = await buildLibrarySeg(nx.number);
        }
        if (next && next.pageCount > 0) setAppended((prev) => [...prev, next!]);
        // Nothing to append (locked/empty next) — stop retrying every frame; the
        // next chapter is still reachable via the nav buttons.
        else appendStoppedRef.current = true;
      } catch { appendStoppedRef.current = true; }
      finally { appendLockRef.current = false; setAppending(false); }
    })();
  }, [settings.infiniteScroll, isContinuous, segments, chapterExistsFrom, isPreview, previewOrder, readableChapters, buildPreviewSeg, buildLibrarySeg]);

  const maybeAppendRef = useRef(maybeAppend);
  useEffect(() => { maybeAppendRef.current = maybeAppend; }, [maybeAppend]);

  // Infinite scroll upward: as the top approaches, prepend the PREVIOUS chapter.
  // Content inserted above the viewport would shift everything down, so we record
  // the pre-insert scrollHeight and re-anchor in a layout effect below.
  const maybePrepend = useCallback(() => {
    if (!settings.infiniteScroll || !isContinuous || prependLockRef.current || prependStoppedRef.current) return;
    const scroller = scrollRef.current;
    if (!scroller) return;
    // Defer to near the top boundary (was ~1 screen).
    if (scroller.scrollTop > scroller.clientHeight * 0.5) return;
    const first = segments[0];
    if (!first || !chapterExistsFrom(first, -1)) return;
    prependLockRef.current = true;
    setPrepending(true);
    (async () => {
      try {
        let prev: Segment | null = null;
        if (isPreview) {
          const pos = previewOrder?.findIndex((c) => c.index === first.previewIndex) ?? -1;
          const px = pos > 0 ? previewOrder?.[pos - 1] : undefined;
          if (px) prev = await buildPreviewSeg(px.index);
        } else {
          const idx = readableChapters.findIndex((c) => c.number === first.chapterNumber);
          const px = idx > 0 ? readableChapters[idx - 1] : undefined;
          if (px?.number != null) prev = await buildLibrarySeg(px.number);
        }
        if (prev && prev.pageCount > 0) {
          prependAdjustRef.current = scrollRef.current?.scrollHeight ?? null;
          setPrepended((p) => [prev!, ...p]);
        } else prependStoppedRef.current = true;
      } catch { prependStoppedRef.current = true; }
      finally { prependLockRef.current = false; setPrepending(false); }
    })();
  }, [settings.infiniteScroll, isContinuous, segments, chapterExistsFrom, isPreview, previewOrder, readableChapters, buildPreviewSeg, buildLibrarySeg]);

  const maybePrependRef = useRef(maybePrepend);
  useEffect(() => { maybePrependRef.current = maybePrepend; }, [maybePrepend]);

  // After a chapter is prepended, push scrollTop down by the inserted height so
  // the reader's viewport doesn't jump.
  useLayoutEffect(() => {
    if (prependAdjustRef.current == null) return;
    const scroller = scrollRef.current;
    if (scroller) {
      const delta = scroller.scrollHeight - prependAdjustRef.current;
      if (delta > 0) scroller.scrollTop += delta;
    }
    prependAdjustRef.current = null;
  }, [prepended]);

  // Sliding window: keep only ±CHAPTER_WINDOW chapters around the active one,
  // dropping the rest (which frees their page images). `active` is the on-screen
  // segment index and `currentGi` the on-screen global page index — the page we
  // anchor the viewport to so pruning content above it doesn't jump the reader.
  const pruneWindow = useCallback((active: number, currentGi: number) => {
    if (!isContinuous || !settings.infiniteScroll) return;
    // Don't fight an in-flight append/prepend or its pending re-anchor.
    if (appendLockRef.current || prependLockRef.current || prependAdjustRef.current != null) return;
    const n = segments.length;
    if (n === 0) return;
    const firstKeep = Math.max(0, active - CHAPTER_WINDOW);
    const lastKeep = Math.min(n - 1, active + CHAPTER_WINDOW);
    const dropFront = firstKeep;
    const dropBack = n - 1 - lastKeep;
    if (dropFront <= 0 && dropBack <= 0) return;

    const P = prepended.length;
    let newPrepended: Segment[];
    let newAppended: Segment[];
    let newHidden: boolean;
    if (primaryHidden) {
      // list = [prep(0..P-1), app(0..A-1)]
      newPrepended = prepended.slice(Math.min(firstKeep, P), Math.min(lastKeep + 1, P));
      newAppended = appended.slice(Math.max(0, firstKeep - P), Math.max(0, lastKeep + 1 - P));
      newHidden = true;
    } else {
      // list = [prep(0..P-1), primary(P), app(0..A-1)]
      const primaryKept = firstKeep <= P && P <= lastKeep;
      newPrepended = prepended.slice(Math.min(firstKeep, P), Math.min(lastKeep + 1, P));
      newAppended = appended.slice(Math.max(0, firstKeep - (P + 1)), Math.max(0, lastKeep + 1 - (P + 1)));
      newHidden = !primaryKept;
    }
    if (newHidden === primaryHidden && newPrepended.length === prepended.length && newAppended.length === appended.length) {
      return; // nothing actually changed
    }

    // Anchor the on-screen page's viewport position across the drop-above so the
    // reader doesn't jump. (getBoundingClientRect is offsetParent-agnostic.)
    if (dropFront > 0) {
      const scroller = scrollRef.current;
      const node = pageRefs.current.get(currentGi);
      if (scroller && node) {
        const pageInSeg = currentGi - (segOffsets[active] ?? 0);
        const newActive = active - dropFront;
        const newSegs = newHidden ? [...newPrepended, ...newAppended] : [...newPrepended, primarySeg, ...newAppended];
        let off = 0;
        for (let k = 0; k < newActive; k++) off += newSegs[k]?.pageCount ?? 0;
        const viewportTop = node.getBoundingClientRect().top - scroller.getBoundingClientRect().top;
        pruneAnchorRef.current = { gi: off + pageInSeg, viewportTop };
        setPruneNonce((x) => x + 1);
      }
    }

    setPrepended(newPrepended);
    setAppended(newAppended);
    if (newHidden !== primaryHidden) setPrimaryHidden(newHidden);
    setActiveSegIndex(active - dropFront);
  }, [isContinuous, settings.infiniteScroll, segments, segOffsets, prepended, appended, primaryHidden, primarySeg]);

  const pruneWindowRef = useRef(pruneWindow);
  useEffect(() => { pruneWindowRef.current = pruneWindow; }, [pruneWindow]);

  // Re-anchor the viewport to the tracked page after a top-side prune.
  useLayoutEffect(() => {
    const a = pruneAnchorRef.current;
    if (!a) return;
    pruneAnchorRef.current = null;
    const scroller = scrollRef.current;
    const node = pageRefs.current.get(a.gi);
    if (scroller && node) {
      const cur = node.getBoundingClientRect().top - scroller.getBoundingClientRect().top;
      scroller.scrollTop += cur - a.viewportTop;
    }
  }, [pruneNonce]);

  // Continuous mode: derive the active segment + page from scroll position, and
  // append the next chapter as the bottom approaches.
  //
  // Page tracking measures against a probe line (a third of the way down) rather
  // than an IntersectionObserver: lazy images are zero-height until they load, so
  // on open every page box stacks at the top and would all intersect at once,
  // latching the counter to the last page (and, since progress follows the page,
  // instantly marking the chapter read). The aspect-ratio boxes give correct
  // heights before a single image loads.
  useEffect(() => {
    if (!isContinuous || totalPages === 0 || loading) return;
    const scroller = scrollRef.current;
    if (!scroller) return;

    let frame = 0;
    const update = () => {
      frame = 0;
      const st = scroller.scrollTop;
      const scrollingUp = st < lastScrollTopRef.current - 2;
      lastScrollTopRef.current = st;
      const box = scroller.getBoundingClientRect();
      const probe = box.top + scroller.clientHeight / 3;
      let current = 0;
      const entries = [...pageRefs.current.entries()].sort((a, b) => a[0] - b[0]);
      for (const [gi, node] of entries) {
        const rect = node.getBoundingClientRect();
        if (rect.top <= probe && rect.bottom > probe) { current = gi; break; }
        if (rect.top > probe) break;      // past the probe — keep the last one below it
        current = gi;
      }
      // Map the global page index back to (segment, page-in-segment).
      let si = 0;
      for (let k = 0; k < segOffsets.length; k++) {
        const off = segOffsets[k];
        if (off != null && current >= off) si = k; else break;
      }
      setActiveSegIndex(si);
      setCurrentPage(current - (segOffsets[si] ?? 0));
      maybeAppendRef.current();
      if (scrollingUp) maybePrependRef.current();
      pruneWindowRef.current(si, current);
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
  }, [isContinuous, totalPages, loading, segOffsets]);

  // Continuous mode: jump to the resume position once the pages are laid out.
  const restoredRef = useRef<string | null>(null);
  useEffect(() => {
    if (!isContinuous || loading || pageCount === 0) return;
    const key = `${seriesId ?? mihonId}:${chapterNumber ?? previewChapterIndex}`;
    if (restoredRef.current === key) return;
    restoredRef.current = key;
    // Land on the resume page when re-opening a partially-read chapter; otherwise
    // force the top. Reading resumePageRef (set at load) instead of the live
    // currentPage avoids inheriting the previous chapter's scroll position.
    const target = resumePageRef.current;
    if (target > 0) pageRefs.current.get(target)?.scrollIntoView();
    else scrollRef.current?.scrollTo({ top: 0 });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isContinuous, loading, pageCount, seriesId, mihonId, chapterNumber, previewChapterIndex]);

  const handleTap = useCallback((e: React.MouseEvent<HTMLDivElement>) => {
    const toggle = () => setChromeVisible((v) => !v);
    if (!settings.tapNavigation) { toggle(); return; }
    const x = e.clientX / window.innerWidth;
    if (isContinuous) {
      // Same left/right layout as paged, but each side scrolls the strip by the
      // adjustable step instead of turning a page: right = forward, left = back.
      const scroller = scrollRef.current;
      if (!scroller) { toggle(); return; }
      const amount = scroller.clientHeight * (settings.tapAdvancePct / 100);
      if (x > 0.7) scroller.scrollBy({ top: amount, behavior: "smooth" });        // forward
      else if (x < 0.3) scroller.scrollBy({ top: -amount, behavior: "smooth" });  // back
      else toggle();
      return;
    }
    if (x < 0.3) advance(isRtl ? 1 : -1);
    else if (x > 0.7) advance(isRtl ? -1 : 1);
    else toggle();
  }, [settings.tapNavigation, settings.tapAdvancePct, isContinuous, isRtl, advance]);

  // ── Actions ───────────────────────────────────────────────────────────
  const toggleBookmark = useCallback(async () => {
    if (isPreview || !seriesId || activeChapterNumber == null || !activeChapterObj) return;
    const num = activeChapterNumber;
    const next = !activeChapterObj.bookmarked;
    try {
      await readerService.setBookmark(seriesId, num, next);
      setChapters((prev) => prev && {
        ...prev,
        chapters: prev.chapters.map((c) => c.number === num ? { ...c, bookmarked: next } : c),
      });
      toast.success(next ? "Bookmarked" : "Bookmark removed");
    } catch {
      toast.error("Failed to update bookmark");
    }
  }, [isPreview, seriesId, activeChapterNumber, activeChapterObj]);

  // Run a reader action (from a hotkey). Page navigation stays WITHIN the
  // chapter; chapter skipping is only the explicit next/prevChapter actions.
  const runAction = useCallback((action: HotkeyAction) => {
    const scroller = scrollRef.current;
    const amount = (scroller?.clientHeight ?? 600) * (settings.tapAdvancePct / 100);
    switch (action) {
      case "nextPage":
        if (isContinuous) scroller?.scrollBy({ top: amount, behavior: "smooth" });
        else advance(1);
        break;
      case "prevPage":
        if (isContinuous) scroller?.scrollBy({ top: -amount, behavior: "smooth" });
        else advance(-1);
        break;
      case "scrollDown":
        if (isContinuous) scroller?.scrollBy({ top: amount, behavior: "smooth" });
        else advance(1);
        break;
      case "scrollUp":
        if (isContinuous) scroller?.scrollBy({ top: -amount, behavior: "smooth" });
        else advance(-1);
        break;
      case "nextChapter":
        if (hasChapter(1)) goToChapter(1);
        break;
      case "prevChapter":
        if (hasChapter(-1)) goToChapter(-1);
        break;
      case "firstPage":
        if (isContinuous) scroller?.scrollTo({ top: 0, behavior: "smooth" });
        else setCurrentPage(0);
        break;
      case "lastPage":
        if (isContinuous) scroller?.scrollTo({ top: scroller.scrollHeight, behavior: "smooth" });
        else setCurrentPage(Math.max(0, pageCount - 1));
        break;
      case "toggleChrome": setChromeVisible((v) => !v); break;
      case "toggleChapters": setChaptersOpen((v) => !v); break;
      case "toggleSettings": setSettingsOpen((v) => !v); break;
      case "bookmark": if (!isPreview) void toggleBookmark(); break;
      case "exit": router.back(); break;
    }
  }, [settings.tapAdvancePct, isContinuous, advance, hasChapter, goToChapter, pageCount, isPreview, toggleBookmark, router]);

  // Keyboard — dispatches through the rebindable hotkey map. In RTL paged mode
  // the physical arrow keys are swapped before lookup so ← always reads forward
  // (manga convention); custom (non-arrow) bindings are taken literally.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return;

      const token = eventKeyToken(e);

      // Rebind mode: capture this key for the pending action instead of acting.
      const pending = capturingRef.current;
      if (pending) {
        e.preventDefault();
        e.stopPropagation();
        if (token === "Escape") { setCapturingAction(null); return; } // cancel
        persistSettings({
          ...settings,
          // Clear this key from any other action so bindings stay unique.
          hotkeys: (() => {
            const next: Hotkeys = { ...settings.hotkeys };
            (Object.keys(next) as HotkeyAction[]).forEach((a) => { if (next[a] === token) next[a] = ""; });
            next[pending] = token;
            return next;
          })(),
        });
        setCapturingAction(null);
        return;
      }

      let lookup = token;
      if (!isContinuous && isRtl) {
        if (token === "ArrowLeft") lookup = "ArrowRight";
        else if (token === "ArrowRight") lookup = "ArrowLeft";
      }
      const hk = settings.hotkeys;
      const action = (Object.keys(hk) as HotkeyAction[]).find((a) => hk[a] === lookup);
      // Keep Space / PageDown / PageUp working as forward/back regardless of
      // rebinding, so a user who clears the arrows doesn't lose basic paging.
      let resolved: HotkeyAction | undefined = action;
      if (!resolved) {
        if (token === "Space" || token === "PageDown") resolved = "nextPage";
        else if (token === "PageUp") resolved = "prevPage";
      }
      if (!resolved) return;
      e.preventDefault();
      runAction(resolved);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [settings, isContinuous, isRtl, runAction, persistSettings]);

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
    if (resolvedMode === "double") {
      const second = currentPage + 1 < pageCount ? [currentPage + 1] : [];
      return [currentPage, ...second];
    }
    return [currentPage];
  }, [resolvedMode, currentPage, pageCount]);

  const preloadPages: number[] = useMemo(() => {
    const out: number[] = [];
    for (let i = 1; i <= settings.preload; i++) {
      const n = currentPage + i;
      if (n < pageCount) out.push(n);
    }
    return out;
  }, [currentPage, settings.preload, pageCount]);

  const lastSeg = segments[segments.length - 1] ?? primarySeg;

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
      ) : (chapter?.locked || streamLocked) ? (
        <LockedChapterScreen
          chapterLabel={chapterLabel}
          url={chapter?.url ?? null}
          hasPrev={hasChapter(-1)}
          hasNext={hasChapter(1)}
          checking={unlockChecking}
          onCheckNow={() => manualUnlockCheckRef.current?.()}
          onPrev={() => goToChapter(-1)}
          onNext={() => goToChapter(1)}
          onExit={() => router.back()}
        />
      ) : isContinuous ? (
        <>
          {settings.infiniteScroll && prepending && (
            <div className="pointer-events-none fixed left-1/2 top-16 z-20 -translate-x-1/2">
              <div className="flex items-center gap-2 rounded-full border border-white/15 bg-black/70 px-3 py-1.5 text-xs font-medium text-white/90 backdrop-blur">
                <Loader2 className="h-3.5 w-3.5 animate-spin" />
                Loading previous chapter…
              </div>
            </div>
          )}
          {settings.infiniteScroll && appending && (
            <div className="pointer-events-none fixed bottom-16 left-1/2 z-20 -translate-x-1/2">
              <div className="flex items-center gap-2 rounded-full border border-white/15 bg-black/70 px-3 py-1.5 text-xs font-medium text-white/90 backdrop-blur">
                <Loader2 className="h-3.5 w-3.5 animate-spin" />
                Loading next chapter…
              </div>
            </div>
          )}
        <div ref={scrollRef} className="h-full overflow-y-auto" onClick={handleTap}>
          <div className="mx-auto flex flex-col items-center" style={{ ...containerWidthStyle, rowGap: gap }}>
            {segments.map((seg, si) => (
              <React.Fragment key={seg.key}>
                {si > 0 && (
                  <div
                    className="w-full px-4 py-8 text-center text-white"
                    onClick={(e) => e.stopPropagation()}
                  >
                    <div className="text-[11px] uppercase tracking-[0.15em] text-white/35">Finished</div>
                    <div className="mt-0.5 text-sm font-medium text-white/70">{segments[si - 1]?.name}</div>
                    <div className="mx-auto my-3 h-px w-16 bg-white/15" />
                    <div className="text-[11px] uppercase tracking-[0.15em] text-white/35">Up next</div>
                    <div className="mt-0.5 text-sm font-medium text-primary/90">{seg.name}</div>
                  </div>
                )}
                {Array.from({ length: seg.pageCount }, (_, i) => {
                  const gi = (segOffsets[si] ?? 0) + i;
                  const near = Math.abs(gi - activeGi) <= IMAGE_WINDOW;
                  return (
                    <div
                      key={gi}
                      data-page={gi}
                      ref={(el) => { if (el) pageRefs.current.set(gi, el); else pageRefs.current.delete(gi); }}
                      className="w-full"
                      style={segPageAspect(seg, gi, i)}
                    >
                      {near && (
                      /* eslint-disable-next-line @next/next/no-img-element */
                      <img
                        src={segPageUrl(seg, i)}
                        alt={`Page ${i + 1}`}
                        loading="lazy"
                        className="block w-full h-auto"
                        onLoad={(e) => {
                          const img = e.currentTarget;
                          img.dataset.retry = "0";
                          onImageLoaded(gi, img.naturalWidth, img.naturalHeight);
                          // Lock the box to the real ratio immediately so the
                          // placeholder height can't leave a gap before the next
                          // scroll-driven re-render picks up the loaded dims.
                          const parent = img.parentElement as HTMLElement | null;
                          if (parent && img.naturalWidth > 0) {
                            parent.style.minHeight = "";
                            parent.style.aspectRatio = `${img.naturalWidth} / ${img.naturalHeight}`;
                          }
                        }}
                        onError={(e) => {
                          // A page can time out on the source (now bounded server-side);
                          // retry a couple of times with a cache-buster instead of
                          // leaving a permanent gap in the strip.
                          const img = e.currentTarget;
                          const tries = Number(img.dataset.retry ?? "0");
                          if (tries >= 3) return;
                          img.dataset.retry = String(tries + 1);
                          const base = segPageUrl(seg, i);
                          const sep = base.includes("?") ? "&" : "?";
                          window.setTimeout(() => { img.src = `${base}${sep}r=${tries + 1}`; }, 1000 * (tries + 1));
                        }}
                      />
                      )}
                    </div>
                  );
                })}
              </React.Fragment>
            ))}
            <EndOfChapter
              chapterLabel={lastSeg.name}
              nextLabel={chapterNameAfter(lastSeg)}
              hasNext={chapterExistsFrom(lastSeg, 1)}
              hasPrev={hasChapter(-1)}
              infinite={settings.infiniteScroll}
              onNext={() => goToChapter(1)}
              onPrev={() => goToChapter(-1)}
              onExit={() => router.back()}
            />
          </div>
        </div>
        </>
      ) : pageCount > 0 && currentPage >= pageCount ? (
        // Chapter transition — its own "page": one click carries into the next
        // chapter (or back to the last page), same as turning any page.
        <div className="flex h-full items-center justify-center overflow-hidden" onClick={handleTap}>
          <ChapterTransition
            finishedLabel={chapterLabel}
            nextLabel={chapterNameAfter(primarySeg)}
            hasNext={hasChapter(1)}
            hasPrev={hasChapter(-1)}
            onNext={() => goToChapter(1)}
            onPrev={() => setCurrentPage(Math.max(0, pageCount - 1))}
            onPrevChapter={() => goToChapter(-1)}
            onExit={() => router.back()}
          />
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
          {/* Preload upcoming pages invisibly. Also feeds smart-detect (onImageLoaded)
              — previously only the single visible page reported its dimensions, so a
              webtoon/manhwa the type-label heuristic didn't catch stayed stuck in
              single-page mode until the user manually flipped through ~6 pages. These
              already-fetched preload images now count toward that sample too. */}
          <div className="hidden">
            {preloadPages.map((i) => (
              // eslint-disable-next-line @next/next/no-img-element
              <img
                key={i}
                src={pageUrl(i)}
                alt=""
                onLoad={(e) => onImageLoaded(i, e.currentTarget.naturalWidth, e.currentTarget.naturalHeight)}
              />
            ))}
          </div>
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
            <div className="truncate text-xs text-white/60">
              {activeLabel}
              {nextChapterName && <span className="text-white/40"> · next: {nextChapterName}</span>}
              {isPreview ? " · Preview" : ""}
            </div>
          </div>
          {isPreview && (
            <button onClick={() => void downloadPreviewChapter()} className="rounded p-1.5 hover:bg-white/10" title="Download this chapter (requires the series in your library)">
              <Download className="h-5 w-5" />
            </button>
          )}
          {!isPreview && activeChapterObj && (
            <button onClick={() => void toggleBookmark()} className="rounded p-1.5 hover:bg-white/10" title={activeChapterObj.bookmarked ? "Remove bookmark" : "Bookmark chapter"}>
              <Bookmark className={`h-5 w-5 ${activeChapterObj.bookmarked ? "fill-current text-pink-400" : ""}`} />
            </button>
          )}
          <button onClick={() => setChaptersOpen((v) => !v)} className="rounded p-1.5 hover:bg-white/10" title="Chapters">
            <List className="h-5 w-5" />
          </button>
          <button onClick={() => setSettingsOpen((v) => !v)} className="rounded p-1.5 hover:bg-white/10" title="Reader settings">
            <Settings2 className="h-5 w-5" />
          </button>
        </div>
      )}

      {/* ── Chapter list drawer ── */}
      {chaptersOpen && (
        <ChapterListDrawer
          isPreview={isPreview}
          previewOrder={previewOrder}
          previewChapterIndex={isContinuous ? (activeSeg.previewIndex ?? previewChapterIndex) : previewChapterIndex}
          libraryChapters={chapters?.chapters ?? null}
          currentNumber={activeChapterNumber}
          onClose={() => setChaptersOpen(false)}
          onPickLibrary={(num) => {
            if (num !== chapterNumber) { setOpeningLabel(null); setChapterNumber(num); }
            setChaptersOpen(false);
          }}
          onPickPreview={(idx) => {
            if (idx !== previewChapterIndex) { setOpeningLabel(null); setPreviewChapterIndex(idx); }
            setChaptersOpen(false);
          }}
        />
      )}

      {/* ── Bottom chrome ── */}
      {chromeVisible && !loading && !error && activePageCount > 0 && (
        <div className="absolute inset-x-0 bottom-0 flex items-center gap-3 bg-black/70 px-4 py-2.5 text-white backdrop-blur">
          <button onClick={() => goToChapter(-1)} className="rounded p-1.5 hover:bg-white/10" title="Previous chapter">
            <ChevronLeft className="h-5 w-5" />
          </button>
          <div className="flex-1" dir={isRtl && !isContinuous ? "rtl" : "ltr"}>
            <Slider
              value={[Math.min(currentPage, Math.max(0, activePageCount - 1))]}
              min={0}
              max={Math.max(0, activePageCount - 1)}
              step={1}
              onValueChange={(v) => {
                const target = v[0] ?? 0;
                setCurrentPage(target);
                if (isContinuous) pageRefs.current.get((segOffsets[activeSegIndex] ?? 0) + target)?.scrollIntoView();
              }}
            />
          </div>
          {settings.showPageNumber && (
            <span className="shrink-0 text-xs tabular-nums text-white/70">
              {Math.min(currentPage + 1, activePageCount)} / {activePageCount}
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

            {isContinuous && (
              <div className="space-y-1.5">
                <Label>Click-to-scroll step — {settings.tapAdvancePct}% of screen</Label>
                <Slider
                  value={[settings.tapAdvancePct]}
                  min={20} max={100} step={10}
                  onValueChange={(v) => persistSettings({ ...settings, tapAdvancePct: v[0] ?? 80 })}
                />
                <p className="text-[11px] text-white/50">
                  Tap the right side (or Space / ↓) to scroll forward by this much; tap the left side (or ↑) to scroll back.
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

            {!isContinuous && (
              <div className="space-y-1.5">
                <Label>Preload pages — {settings.preload}</Label>
                <Slider value={[settings.preload]} min={0} max={10} step={1}
                  onValueChange={(v) => persistSettings({ ...settings, preload: v[0] ?? 4 })} />
              </div>
            )}

            <ToggleRow label="Tap zones (sides turn pages · tap to scroll)" checked={settings.tapNavigation}
              onChange={(v) => persistSettings({ ...settings, tapNavigation: v })} />
            {isContinuous && (
              <ToggleRow label="Infinite scroll (roll into next chapter)" checked={settings.infiniteScroll}
                onChange={(v) => persistSettings({ ...settings, infiniteScroll: v })} />
            )}
            <ToggleRow label="Show page number" checked={settings.showPageNumber}
              onChange={(v) => persistSettings({ ...settings, showPageNumber: v })} />
            {!isContinuous && (
              <ToggleRow label="Chapter transition screen (finished · up next)" checked={settings.chapterTransition}
                onChange={(v) => persistSettings({ ...settings, chapterTransition: v })} />
            )}
            {!isPreview && (
              <ToggleRow label="Mark read on last page" checked={settings.autoMarkRead}
                onChange={(v) => persistSettings({ ...settings, autoMarkRead: v })} />
            )}

            {/* ── Keyboard shortcuts ── */}
            <div className="border-t border-white/10 pt-3">
              <div className="mb-2 flex items-center justify-between">
                <Label>Keyboard shortcuts</Label>
                <button
                  onClick={() => persistSettings({ ...settings, hotkeys: { ...DEFAULT_HOTKEYS } })}
                  className="text-[11px] text-white/50 underline-offset-2 hover:text-white hover:underline"
                >
                  Reset defaults
                </button>
              </div>
              <div className="space-y-1">
                {HOTKEY_ACTIONS.map(({ action, label }) => (
                  <div key={action} className="flex items-center justify-between gap-2">
                    <span className="truncate text-[13px] text-white/80">{label}</span>
                    <button
                      onClick={() => setCapturingAction((cur) => (cur === action ? null : action))}
                      className={`min-w-[64px] shrink-0 rounded border px-2 py-1 text-center text-xs font-medium transition-colors ${
                        capturingAction === action
                          ? "border-pink-400 bg-pink-400/20 text-pink-200 animate-pulse"
                          : "border-white/15 bg-zinc-800 text-white hover:bg-zinc-700"
                      }`}
                      title="Click, then press a key to rebind"
                    >
                      {capturingAction === action ? "Press a key…" : keyLabel(settings.hotkeys[action])}
                    </button>
                  </div>
                ))}
              </div>
              <p className="mt-2 text-[11px] text-white/50">
                Click a key, then press the new one. Esc while rebinding cancels. Arrow keys turn
                pages / scroll — they never skip chapters. In right-to-left mode ← reads forward.
              </p>
            </div>

            {/* ── Cache ── */}
            <div className="border-t border-white/10 pt-3">
              <Label>Streamed image cache</Label>
              <p className="mt-1 mb-2 text-[11px] text-white/50">
                Pages read live from a source (not downloaded) are cached in memory for smooth
                scrolling. Clear it if a source served a stale or broken image.
              </p>
              <ToggleRow label="Clear the cache when I exit the reader" checked={settings.autoClearCache}
                onChange={(v) => persistSettings({ ...settings, autoClearCache: v })} />
              <div className="mt-2" />
              <Button
                variant="secondary" size="sm"
                className="w-full"
                disabled={clearingCache}
                onClick={async () => {
                  setClearingCache(true);
                  try {
                    const res = await readerService.clearStreamCache();
                    toast.success(res.cleared > 0 ? `Cleared ${res.cleared} cached page image${res.cleared === 1 ? "" : "s"}.` : "Cache was already empty.");
                  } catch {
                    toast.error("Couldn't clear the cache.");
                  } finally {
                    setClearingCache(false);
                  }
                }}
              >
                {clearingCache ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Trash2 className="mr-2 h-4 w-4" />}
                Clear reader cache (web pages)
              </Button>
            </div>

            {!isPreview && activeChapterObj && (
              <div className="border-t border-white/10 pt-3">
                <Button
                  variant="secondary" size="sm" className="w-full"
                  onClick={() => {
                    if (!seriesId || activeChapterNumber == null) return;
                    const num = activeChapterNumber;
                    const nextCompleted = !activeChapterObj.isCompleted;
                    void readerService.markChapters(seriesId, [num], nextCompleted).then(() => {
                      setChapters((prev) => prev && {
                        ...prev,
                        chapters: prev.chapters.map((c) => c.number === num ? { ...c, isCompleted: nextCompleted, progress: nextCompleted ? 1 : 0 } : c),
                      });
                    });
                  }}
                >
                  {activeChapterObj.isCompleted ? "Mark chapter unread" : "Mark chapter read"}
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
 * Footer of a continuous (webtoon / long-strip / vertical) chapter. With infinite
 * scroll on, the next chapter appends automatically before this is reached, so it
 * mostly surfaces at the very end of a series or when appending failed.
 */
function EndOfChapter({
  chapterLabel, nextLabel, hasNext, hasPrev, infinite, onNext, onPrev, onExit,
}: {
  chapterLabel: string;
  nextLabel: string | null;
  hasNext: boolean;
  hasPrev: boolean;
  infinite: boolean;
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
        <div className="text-xs uppercase tracking-[0.15em] text-white/35">Finished</div>
        <div className="mt-1 text-sm font-medium text-white/80">{chapterLabel}</div>
      </div>
      {hasNext && nextLabel && (
        <div>
          <div className="text-xs uppercase tracking-[0.15em] text-white/35">Up next</div>
          <div className="mt-1 text-sm font-medium text-primary/90">{nextLabel}</div>
        </div>
      )}
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
      {hasNext && <p className="text-xs text-white/35">{infinite ? "keep scrolling to continue" : "or press →"}</p>}
    </div>
  );
}

/**
 * Paged-mode chapter transition — rendered as a standalone "page" between two
 * chapters. Taps flow through to the reader's page-turn zones (so one click in
 * the forward zone enters the next chapter), while the explicit buttons give a
 * direct way to jump forward/back.
 */
function ChapterTransition({
  finishedLabel, nextLabel, hasNext, hasPrev, onNext, onPrev, onPrevChapter, onExit,
}: {
  finishedLabel: string;
  nextLabel: string | null;
  hasNext: boolean;
  hasPrev: boolean;
  onNext: () => void;
  onPrev: () => void;
  onPrevChapter: () => void;
  onExit: () => void;
}) {
  return (
    <div className="flex h-full w-full flex-col items-center justify-center gap-6 px-6 text-center text-white">
      <div>
        <div className="text-xs uppercase tracking-[0.15em] text-white/35">Finished</div>
        <div className="mt-1 text-base font-medium text-white/85">{finishedLabel}</div>
      </div>
      {hasNext && nextLabel ? (
        <div>
          <div className="text-xs uppercase tracking-[0.15em] text-white/35">Up next</div>
          <div className="mt-1 text-base font-medium text-primary">{nextLabel}</div>
        </div>
      ) : (
        <div className="text-sm text-white/50">You&apos;re all caught up.</div>
      )}
      <div className="flex flex-wrap items-center justify-center gap-2">
        <Button variant="secondary" onClick={(e) => { e.stopPropagation(); onPrev(); }} className="gap-1.5">
          <ChevronLeft className="h-4 w-4" />
          Back a page
        </Button>
        {hasPrev && (
          <Button variant="secondary" onClick={(e) => { e.stopPropagation(); onPrevChapter(); }}>
            Previous chapter
          </Button>
        )}
        {hasNext ? (
          <Button onClick={(e) => { e.stopPropagation(); onNext(); }} className="gap-1.5">
            Next chapter
            <ChevronRight className="h-4 w-4" />
          </Button>
        ) : (
          <Button variant="secondary" onClick={(e) => { e.stopPropagation(); onExit(); }}>
            Back to series
          </Button>
        )}
      </div>
    </div>
  );
}

/**
 * Locked/paid chapter view. Purchase happens on the source site (extensions
 * expose no in-app buy), so the button opens it in a new tab — window.open is
 * primary (the app's proven cross-wrapper path for web/mobile/exe) and the
 * anchor href is the backup when a wrapper blocks the popup. Living in the reader
 * means unlocking never requires exiting and re-entering.
 */
function LockedChapterScreen({
  chapterLabel, url, hasPrev, hasNext, checking, onCheckNow, onPrev, onNext, onExit,
}: {
  chapterLabel: string;
  url: string | null;
  hasPrev: boolean;
  hasNext: boolean;
  checking: boolean;
  onCheckNow: () => void;
  onPrev: () => void;
  onNext: () => void;
  onExit: () => void;
}) {
  return (
    <div className="flex h-full w-full flex-col items-center justify-center gap-6 px-6 text-center text-white">
      <Lock className="h-10 w-10 text-violet-400" />
      <div>
        <div className="text-xs uppercase tracking-[0.15em] text-white/35">Locked chapter</div>
        <div className="mt-1 text-base font-medium text-white/85">{chapterLabel}</div>
        <p className="mx-auto mt-2 max-w-xs text-sm text-white/50">
          This is a paid chapter. Buy it on the source site, then come back to this
          tab — it checks automatically and opens the chapter once it&apos;s unlocked.
        </p>
      </div>
      {url ? (
        <a
          href={url}
          target="_blank"
          rel="noopener noreferrer"
          onClick={(e) => {
            const w = window.open(url, "_blank", "noopener,noreferrer");
            if (w) e.preventDefault(); // popup handled it; otherwise let the link navigate
          }}
          className="inline-flex items-center gap-2 rounded-full bg-violet-500 px-5 py-2.5 text-sm font-medium text-white shadow-lg transition-colors hover:bg-violet-400"
        >
          <ExternalLink className="h-4 w-4" />
          Buy / unlock on source
        </a>
      ) : (
        <div className="text-sm text-white/40">No purchase link is available from the source.</div>
      )}
      <button
        type="button"
        onClick={onCheckNow}
        disabled={checking}
        className="inline-flex items-center gap-2 rounded-full border border-white/20 bg-white/[0.06] px-4 py-2 text-sm text-white/80 transition-colors hover:bg-white/[0.12] disabled:cursor-not-allowed disabled:opacity-60"
      >
        {checking ? (
          <>
            <span className="h-3.5 w-3.5 animate-spin rounded-full border-2 border-white/30 border-t-white/90" />
            Checking…
          </>
        ) : (
          "I bought it — check again"
        )}
      </button>
      <div className="flex flex-wrap items-center justify-center gap-2">
        {hasPrev && (
          <Button variant="secondary" onClick={onPrev} className="gap-1.5">
            <ChevronLeft className="h-4 w-4" />
            Previous
          </Button>
        )}
        {hasNext && (
          <Button variant="secondary" onClick={onNext} className="gap-1.5">
            Next
            <ChevronRight className="h-4 w-4" />
          </Button>
        )}
        <Button variant="ghost" onClick={onExit} className="text-white/70 hover:text-white">
          Back to series
        </Button>
      </div>
    </div>
  );
}

/**
 * Slide-in list of every chapter with a Read/jump button and read-state marks.
 * Works for both library reading (numbered chapters, progress/bookmarks) and
 * preview reading (source chapter order). Newest chapters at the top.
 */
function ChapterListDrawer({
  isPreview, previewOrder, previewChapterIndex, libraryChapters, currentNumber,
  onClose, onPickLibrary, onPickPreview,
}: {
  isPreview: boolean;
  previewOrder: PreviewChapter[] | null;
  previewChapterIndex: number;
  libraryChapters: ReaderChapter[] | null;
  currentNumber: number | null;
  onClose: () => void;
  onPickLibrary: (num: number) => void;
  onPickPreview: (idx: number) => void;
}) {
  const [query, setQuery] = useState("");
  const activeRef = useRef<HTMLButtonElement>(null);
  const q = query.trim().toLowerCase();

  // Newest first for display, regardless of the internal reading order.
  const libRows = useMemo(() => {
    const rows = [...(libraryChapters ?? [])].sort((a, b) => b.number - a.number);
    if (!q) return rows;
    return rows.filter((c) => String(c.number).includes(q) || (c.name ?? "").toLowerCase().includes(q));
  }, [libraryChapters, q]);
  const pvRows = useMemo(() => {
    const rows = [...(previewOrder ?? [])].sort((a, b) => b.index - a.index);
    if (!q) return rows;
    return rows.filter((c) => String(c.index + 1).includes(q) || (c.name ?? "").toLowerCase().includes(q));
  }, [previewOrder, q]);

  // Jump to the current chapter when the drawer opens.
  useEffect(() => {
    activeRef.current?.scrollIntoView({ block: "center" });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const rowCount = isPreview ? pvRows.length : libRows.length;

  return (
    <div className="absolute right-0 top-12 bottom-0 z-20 flex w-80 max-w-[90vw] flex-col bg-zinc-900/95 text-white backdrop-blur border-l border-white/10">
      <div className="flex items-center justify-between px-4 py-3 border-b border-white/10">
        <h2 className="text-sm font-semibold">Chapters</h2>
        <button onClick={onClose} className="rounded p-1 hover:bg-white/10"><X className="h-4 w-4" /></button>
      </div>
      <div className="border-b border-white/10 px-3 py-2">
        <div className="relative">
          <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-white/40" />
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Filter by number or title…"
            className="w-full rounded-md border border-white/10 bg-white/[0.05] py-1.5 pl-8 pr-2 text-sm text-white placeholder:text-white/35 focus:outline-none focus:ring-1 focus:ring-white/20"
          />
        </div>
      </div>
      <div className="flex-1 overflow-y-auto py-1">
        {rowCount === 0 ? (
          <div className="px-4 py-8 text-center text-xs text-white/40">No chapters match.</div>
        ) : isPreview
          ? pvRows.map((c) => {
              const active = c.index === previewChapterIndex;
              return (
                <button
                  key={c.index}
                  ref={active ? activeRef : undefined}
                  onClick={() => onPickPreview(c.index)}
                  className={`flex w-full items-center gap-2 px-4 py-2 text-left text-sm hover:bg-white/[0.06] ${active ? "bg-white/[0.08] font-medium" : ""}`}
                >
                  <span className="min-w-0 flex-1 truncate">{c.name || `Chapter ${c.index + 1}`}</span>
                  {active && <span className="shrink-0 text-[11px] text-primary">reading</span>}
                </button>
              );
            })
          : libRows.map((c) => {
              const active = c.number === currentNumber;
              return (
                // Every numbered chapter is selectable — undownloaded ones stream
                // live from the source (or show the buy screen when locked).
                <button
                  key={c.number}
                  ref={active ? activeRef : undefined}
                  onClick={() => onPickLibrary(c.number)}
                  className={`flex w-full items-center gap-2 px-4 py-2 text-left text-sm hover:bg-white/[0.06] ${active ? "bg-white/[0.08]" : ""}`}
                >
                  <span className="w-4 shrink-0">
                    {c.isCompleted
                      ? <Check className="h-3.5 w-3.5 text-emerald-400" />
                      : c.progress > 0
                        ? <span className="text-[10px] tabular-nums text-primary">{Math.round(c.progress * 100)}%</span>
                        : null}
                  </span>
                  <span className={`min-w-0 flex-1 truncate ${c.isCompleted ? "text-white/50" : ""} ${active ? "font-medium" : ""}`}>
                    {c.name || `Chapter ${c.number}`}
                  </span>
                  {c.locked
                    ? <Lock className="h-3 w-3 shrink-0 text-violet-400" />
                    : !c.filename && <span className="shrink-0 text-[10px] uppercase tracking-wide text-white/30" title="Not downloaded — streams live">web</span>}
                  {c.bookmarked && <Bookmark className="h-3 w-3 shrink-0 fill-pink-500 text-pink-500" />}
                  {active && <span className="shrink-0 text-[11px] text-primary">reading</span>}
                </button>
              );
            })}
      </div>
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
