"use client";

import React, { memo, useMemo, useState } from 'react';
import Image from 'next/image';
import { useRouter } from 'next/navigation';
import { BookOpen, Check, ChevronDown, ChevronRight } from 'lucide-react';
import { useHistoryFeed } from '@/lib/api/hooks/useSeries';
import { useSearch } from '@/contexts/search-context';
import { useAuth } from '@/contexts/auth-context';
import { formatThumbnailUrl } from '@/lib/utils/thumbnail';
import { type HistoryChapter, type HistoryFeedItem } from '@/lib/api/types';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import {
  normalizeUtcString,
  formatRelativeTime,
  getDateBucket,
  BUCKET_LABELS,
  type DateBucket,
} from '@/components/comp/queue/utils';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

// The server caps the feed at 500 ENTRIES and stacks before capping, so asking
// for more than that gains nothing. Stacking and capping deliberately do NOT
// happen here: a client-side pass would re-split runs the server already
// grouped, and a client-side cap would count a binge as many entries again.
const FETCH_LIMIT = 500;

const BUCKET_ORDER: DateBucket[] = ['today', 'yesterday', 'this-week', 'earlier'];

// ---------------------------------------------------------------------------
// Row
// ---------------------------------------------------------------------------

interface FeedRow {
  key: string;
  item: HistoryFeedItem;
  sortTime: number;
  displayTime: string;
}

/** What a chapter row says under the title, whether stacked or not. */
function chapterLabel(chapterName?: string, chapterNumber?: number): string {
  return (
    chapterName ||
    (chapterNumber !== undefined && chapterNumber !== null ? `Chapter ${chapterNumber}` : 'Chapter')
  );
}

/**
 * Part-read chapters are the ones worth returning to, so they are called out
 * rather than left to look identical to a finished one.
 */
function progressLabel(progress: number, completed: boolean): string | null {
  if (completed) return null;
  if (!progress || progress <= 0) return null;
  return `${Math.round(progress * 100)}%`;
}

const HistoryRow = memo(function HistoryRow({
  row,
  onOpen,
}: {
  row: FeedRow;
  onOpen: (seriesId: string) => void;
}) {
  const { item } = row;
  const label = chapterLabel(item.chapterName, item.chapterNumber);
  const pct = progressLabel(item.progress, item.completed);

  return (
    <button
      type="button"
      onClick={() => onOpen(item.seriesId)}
      className="w-full flex items-center gap-3 px-3 py-2 text-left transition-colors hover:bg-white/[0.03] focus-visible:outline-none focus-visible:bg-white/[0.05]"
    >
      {/* Cover */}
      <div className="h-14 w-10 shrink-0 overflow-hidden rounded-md bg-white/[0.04]">
        <Image
          src={formatThumbnailUrl(item.thumbnailUrl)}
          alt={item.seriesTitle}
          width={40}
          height={56}
          className="object-cover w-full h-full"
          onError={(e) => {
            (e.target as HTMLImageElement).src = '/renzo.png';
          }}
        />
      </div>

      {/* Title / subtitle */}
      <div className="min-w-0 flex-1">
        <div className="truncate text-sm font-medium">{item.seriesTitle}</div>
        <div className="mt-0.5 flex items-center gap-1.5 text-xs text-muted-foreground">
          <BookOpen className="h-3.5 w-3.5 shrink-0" />
          <span className="truncate">
            {label}
            {pct ? ` · ${pct}` : ''}
          </span>
        </div>
      </div>

      {/* Time */}
      <div className="shrink-0 flex items-center gap-1.5 text-xs tabular-nums text-muted-foreground/70">
        {item.completed && <Check className="h-3.5 w-3.5 text-primary/70" aria-label="Finished" />}
        {row.displayTime}
      </div>
    </button>
  );
});

// ---------------------------------------------------------------------------
// Chapter row inside an expanded stack
// ---------------------------------------------------------------------------

const StackChapterRow = memo(function StackChapterRow({
  seriesId,
  seriesTitle,
  thumbnailUrl,
  chapter,
  onOpen,
}: {
  seriesId: string;
  seriesTitle: string;
  thumbnailUrl?: string;
  chapter: HistoryChapter;
  onOpen: (seriesId: string) => void;
}) {
  const label = chapterLabel(chapter.chapterName, chapter.chapterNumber);
  const pct = progressLabel(chapter.progress, chapter.completed);
  const parsed = Date.parse(normalizeUtcString(chapter.readAt));
  const when = formatRelativeTime(new Date(Number.isNaN(parsed) ? Date.now() : parsed));

  return (
    <button
      type="button"
      onClick={() => onOpen(seriesId)}
      className="w-full flex items-center gap-3 px-3 py-2 text-left transition-colors hover:bg-white/[0.03] focus-visible:outline-none focus-visible:bg-white/[0.05]"
    >
      <div className="h-14 w-10 shrink-0 overflow-hidden rounded-md bg-white/[0.04]">
        <Image
          src={formatThumbnailUrl(thumbnailUrl)}
          alt={seriesTitle}
          width={40}
          height={56}
          className="object-cover w-full h-full"
          onError={(e) => {
            (e.target as HTMLImageElement).src = '/renzo.png';
          }}
        />
      </div>
      <div className="min-w-0 flex-1">
        <div className="truncate text-sm font-medium">{seriesTitle}</div>
        <div className="mt-0.5 flex items-center gap-1.5 text-xs text-muted-foreground">
          <BookOpen className="h-3.5 w-3.5 shrink-0" />
          <span className="truncate">
            {label}
            {pct ? ` · ${pct}` : ''}
          </span>
        </div>
      </div>
      <div className="shrink-0 flex items-center gap-1.5 text-xs tabular-nums text-muted-foreground/70">
        {chapter.completed && <Check className="h-3.5 w-3.5 text-primary/70" aria-label="Finished" />}
        {when}
      </div>
    </button>
  );
});

// ---------------------------------------------------------------------------
// Stack (a run of 5+ chapters read back-to-back from one series)
// ---------------------------------------------------------------------------

const HistoryStack = memo(function HistoryStack({
  row,
  onOpen,
}: {
  row: FeedRow;
  onOpen: (seriesId: string) => void;
}) {
  const [isOpen, setIsOpen] = useState(false);
  const { item } = row;
  const chapters = item.chapters ?? [];
  const count = chapters.length;

  const numbers = chapters
    .map((c) => c.chapterNumber)
    .filter((n): n is number => n !== undefined && n !== null);
  const minChapter = numbers.length ? Math.min(...numbers) : undefined;
  const maxChapter = numbers.length ? Math.max(...numbers) : undefined;
  const rangeLabel =
    minChapter !== undefined && maxChapter !== undefined
      ? minChapter === maxChapter
        ? `Chapter ${minChapter}`
        : `Chapters ${minChapter}-${maxChapter}`
      : `${count} chapters`;

  return (
    <Collapsible open={isOpen} onOpenChange={setIsOpen}>
      <div className="overflow-hidden rounded-lg">
        <CollapsibleTrigger asChild>
          <button
            type="button"
            className="w-full flex items-center gap-3 px-3 py-2 text-left transition-colors hover:bg-white/[0.03] focus-visible:outline-none focus-visible:bg-white/[0.05]"
          >
            <div className="h-14 w-10 shrink-0 overflow-hidden rounded-md bg-white/[0.04]">
              <Image
                src={formatThumbnailUrl(item.thumbnailUrl)}
                alt={item.seriesTitle}
                width={40}
                height={56}
                className="object-cover w-full h-full"
                onError={(e) => {
                  (e.target as HTMLImageElement).src = '/renzo.png';
                }}
              />
            </div>
            <div className="min-w-0 flex-1">
              <div className="truncate text-sm font-medium">{item.seriesTitle}</div>
              <div className="mt-0.5 flex items-center gap-1.5 text-xs text-muted-foreground">
                <BookOpen className="h-3.5 w-3.5 shrink-0" />
                <span className="truncate">
                  {rangeLabel} · {count} read
                </span>
              </div>
            </div>
            <div className="shrink-0 flex items-center gap-2 text-xs tabular-nums text-muted-foreground/70">
              {row.displayTime}
              {isOpen ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
            </div>
          </button>
        </CollapsibleTrigger>
        <CollapsibleContent>
          <div className="border-t border-white/[0.04] pl-6">
            {chapters.map((c, idx) => (
              <StackChapterRow
                key={`${item.seriesId}-${c.chapterNumber ?? idx}-${idx}`}
                seriesId={item.seriesId}
                seriesTitle={item.seriesTitle}
                thumbnailUrl={item.thumbnailUrl}
                chapter={c}
                onOpen={onOpen}
              />
            ))}
          </div>
        </CollapsibleContent>
      </div>
    </Collapsible>
  );
});

// ---------------------------------------------------------------------------
// Section group
// ---------------------------------------------------------------------------

const SectionGroup = memo(function SectionGroup({
  label,
  rows,
  onOpen,
}: {
  label: string;
  rows: FeedRow[];
  onOpen: (seriesId: string) => void;
}) {
  if (rows.length === 0) return null;
  return (
    <section className="mb-10">
      <div className="px-1 pb-2 text-[11px] uppercase tracking-[0.08em] text-muted-foreground">
        {label}
      </div>
      <div className="rounded-lg overflow-hidden">
        {rows.map((row) =>
          row.item.kind === 'stack' ? (
            <HistoryStack key={row.key} row={row} onOpen={onOpen} />
          ) : (
            <HistoryRow key={row.key} row={row} onOpen={onOpen} />
          )
        )}
      </div>
    </section>
  );
});

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export default function HistoryPage() {
  const router = useRouter();
  const { debouncedSearchTerm } = useSearch();
  const search = debouncedSearchTerm.trim().toLowerCase();

  // Reading history is per-user by definition. Owner-level accounts can flip to
  // everyone's, same as Updates, for support.
  const { canOwner } = useAuth();
  const [viewAllLibraries, setViewAllLibraries] = useState(false);
  const { data, isLoading } = useHistoryFeed(0, FETCH_LIMIT, canOwner && viewAllLibraries);

  const rows = useMemo<FeedRow[]>(() => {
    const items = data ?? [];
    const result: FeedRow[] = [];
    for (let i = 0; i < items.length; i++) {
      const item = items[i];
      if (!item) continue;
      if (search && !item.seriesTitle.toLowerCase().includes(search)) continue;
      const parsed = Date.parse(normalizeUtcString(item.readAt));
      const sortTime = Number.isNaN(parsed) ? Date.now() : parsed;
      result.push({
        key: `${item.seriesId}-${item.kind}-${item.chapterNumber ?? ''}-${i}`,
        item,
        sortTime,
        displayTime: formatRelativeTime(new Date(sortTime)),
      });
    }
    return result;
  }, [data, search]);

  const buckets = useMemo(() => {
    const groups: Record<DateBucket, FeedRow[]> = {
      today: [],
      yesterday: [],
      'this-week': [],
      earlier: [],
    };
    for (const row of rows) {
      groups[getDateBucket(row.sortTime)].push(row);
    }
    return groups;
  }, [rows]);

  const handleOpen = (seriesId: string) => {
    router.push(`/library/series?id=${seriesId}`);
  };

  // A stack is one entry but many chapters; the header counts what was actually
  // read, which is the number a reader cares about.
  const chapterCount = useMemo(
    () => rows.reduce((n, r) => n + (r.item.kind === 'stack' ? (r.item.chapters?.length ?? 0) : 1), 0),
    [rows],
  );

  return (
    <div className="mx-auto max-w-[1100px] py-6 sm:py-10">
      {/* Header */}
      <header className="mb-8">
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-[22px] font-semibold tracking-tight">History</h1>
          <span className="text-sm tabular-nums text-muted-foreground/70">
            {chapterCount}
          </span>
          {canOwner && (
            <button
              type="button"
              onClick={() => setViewAllLibraries((v) => !v)}
              aria-pressed={viewAllLibraries}
              title={viewAllLibraries ? "Showing every user's history — click to view only your own" : "Showing only your history — click to view every user's"}
              className={`ml-auto inline-flex items-center gap-1.5 rounded-full border px-3 py-1.5 text-xs font-medium transition-colors ${
                viewAllLibraries
                  ? "border-primary/40 bg-primary/15 text-primary"
                  : "border-border/40 bg-foreground/[0.04] text-muted-foreground hover:bg-foreground/[0.06] hover:text-foreground"
              }`}
            >
              {viewAllLibraries ? "All libraries" : "My library"}
            </button>
          )}
        </div>
      </header>

      {/* Body */}
      {isLoading ? (
        <div className="text-center text-xs text-muted-foreground py-16">Loading…</div>
      ) : rows.length === 0 ? (
        <div className="text-center text-xs text-muted-foreground py-16">
          {search
            ? `No history matching "${debouncedSearchTerm.trim()}".`
            : 'Nothing read yet — chapters you open will show up here.'}
        </div>
      ) : (
        BUCKET_ORDER.map((bucket) => (
          <SectionGroup
            key={bucket}
            label={BUCKET_LABELS[bucket]}
            rows={buckets[bucket]}
            onOpen={handleOpen}
          />
        ))
      )}
    </div>
  );
}
