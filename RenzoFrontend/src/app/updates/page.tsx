"use client";

import React, { memo, useEffect, useMemo, useRef, useState } from 'react';
import Image from 'next/image';
import { useRouter } from 'next/navigation';
import { BookPlus, BookOpen, ChevronDown, ChevronRight, Loader2, RefreshCw, Check } from 'lucide-react';
import { toast } from 'sonner';
import { seriesService } from '@/lib/api/services/seriesService';
import { useUpdatesFeed } from '@/lib/api/hooks/useSeries';
import { useSearch } from '@/contexts/search-context';
import { useAuth } from '@/contexts/auth-context';
import { formatThumbnailUrl } from '@/lib/utils/thumbnail';
import { type UpdateFeedItem } from '@/lib/api/types';
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

// Pull a deep feed so batch releases (many chapters at once for one series) and
// older, previously-missed updates are all visible without paging.
const FETCH_LIMIT = 1000;

// A run of this many-or-more consecutive "newChapter" entries for the same
// series collapses into a single expandable stack, so a 20-chapter batch
// release doesn't push everything else off the page.
const STACK_THRESHOLD = 5;

const BUCKET_ORDER: DateBucket[] = ['today', 'yesterday', 'this-week', 'earlier'];

// ---------------------------------------------------------------------------
// Row
// ---------------------------------------------------------------------------

interface FeedRow {
  key: string;
  item: UpdateFeedItem;
  sortTime: number;
  displayTime: string;
}

const UpdateRow = memo(function UpdateRow({
  row,
  onOpen,
}: {
  row: FeedRow;
  onOpen: (seriesId: string) => void;
}) {
  const { item } = row;
  const isAdded = item.kind === 'seriesAdded';
  // Finished chapters stay in the feed (and stay clickable) but read greyed-out.
  const isRead = !isAdded && item.read === true;

  const chapterLabel = isAdded
    ? 'Added to library'
    : item.chapterName ||
      (item.chapterNumber !== undefined && item.chapterNumber !== null
        ? `Chapter ${item.chapterNumber}`
        : 'New chapter');

  return (
    <button
      type="button"
      onClick={() => onOpen(item.seriesId)}
      className={`w-full flex items-center gap-3 px-3 py-2 text-left transition-colors hover:bg-white/[0.03] focus-visible:outline-none focus-visible:bg-white/[0.05] ${isRead ? 'opacity-45' : ''}`}
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
          {isAdded ? (
            <BookPlus className="h-3.5 w-3.5 shrink-0 text-primary/80" />
          ) : (
            <BookOpen className="h-3.5 w-3.5 shrink-0" />
          )}
          <span className="truncate">
            {chapterLabel}
            {!isAdded && item.provider ? ` · ${item.provider}` : ''}
          </span>
        </div>
      </div>

      {/* Time */}
      <div className="shrink-0 flex items-center gap-1.5 text-xs tabular-nums text-muted-foreground/70">
        {isRead && <Check className="h-3.5 w-3.5 text-primary/70" aria-label="Read" />}
        {row.displayTime}
      </div>
    </button>
  );
});

// ---------------------------------------------------------------------------
// Stack (collapsed run of 5+ consecutive chapters for the same series)
// ---------------------------------------------------------------------------

interface StackGroup {
  type: 'stack';
  key: string;
  seriesId: string;
  seriesTitle: string;
  thumbnailUrl?: string;
  provider?: string;
  rows: FeedRow[]; // sorted largest (latest) chapter first
  minChapter?: number;
  maxChapter?: number;
  displayTime: string;
}

interface SingleGroup {
  type: 'single';
  row: FeedRow;
}

type DisplayGroup = StackGroup | SingleGroup;

// Collapses runs of STACK_THRESHOLD-or-more consecutive "newChapter" entries
// for the same series into a single stack. Rows arrive latest-first overall,
// but within a stack we sort strictly by chapter number descending so the
// expanded view is guaranteed largest/latest-first even if a batch's
// timestamps and chapter numbers aren't perfectly aligned.
function groupConsecutiveRows(rows: FeedRow[], bucketKey: string): DisplayGroup[] {
  const groups: DisplayGroup[] = [];
  let i = 0;
  while (i < rows.length) {
    const row = rows[i];
    if (!row) {
      i++;
      continue;
    }
    if (row.item.kind === 'newChapter') {
      let j = i + 1;
      while (
        j < rows.length &&
        rows[j]?.item.kind === 'newChapter' &&
        rows[j]?.item.seriesId === row.item.seriesId
      ) {
        j++;
      }
      const run = rows.slice(i, j);
      if (run.length >= STACK_THRESHOLD) {
        const chapterNumbers = run
          .map((r) => r.item.chapterNumber)
          .filter((n): n is number => n !== undefined && n !== null);
        const sortedRows = [...run].sort((a, b) => {
          const an = a.item.chapterNumber ?? -Infinity;
          const bn = b.item.chapterNumber ?? -Infinity;
          return bn - an;
        });
        const latestTime = Math.max(...run.map((r) => r.sortTime));
        groups.push({
          type: 'stack',
          key: `stack-${bucketKey}-${row.item.seriesId}-${i}`,
          seriesId: row.item.seriesId,
          seriesTitle: row.item.seriesTitle,
          thumbnailUrl: row.item.thumbnailUrl,
          provider: row.item.provider,
          rows: sortedRows,
          minChapter: chapterNumbers.length ? Math.min(...chapterNumbers) : undefined,
          maxChapter: chapterNumbers.length ? Math.max(...chapterNumbers) : undefined,
          displayTime: formatRelativeTime(new Date(latestTime)),
        });
        i = j;
        continue;
      }
    }
    groups.push({ type: 'single', row });
    i++;
  }
  return groups;
}

const UpdateStack = memo(function UpdateStack({
  group,
  onOpen,
}: {
  group: StackGroup;
  onOpen: (seriesId: string) => void;
}) {
  const [isOpen, setIsOpen] = useState(false);
  const count = group.rows.length;
  const rangeLabel =
    group.minChapter !== undefined && group.maxChapter !== undefined
      ? group.minChapter === group.maxChapter
        ? `Chapter ${group.minChapter}`
        : `Chapters ${group.minChapter}-${group.maxChapter}`
      : `${count} new chapters`;

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
                src={formatThumbnailUrl(group.thumbnailUrl)}
                alt={group.seriesTitle}
                width={40}
                height={56}
                className="object-cover w-full h-full"
                onError={(e) => {
                  (e.target as HTMLImageElement).src = '/renzo.png';
                }}
              />
            </div>
            <div className="min-w-0 flex-1">
              <div className="truncate text-sm font-medium">{group.seriesTitle}</div>
              <div className="mt-0.5 flex items-center gap-1.5 text-xs text-muted-foreground">
                <BookOpen className="h-3.5 w-3.5 shrink-0" />
                <span className="truncate">
                  {rangeLabel} · {count} new{group.provider ? ` · ${group.provider}` : ''}
                </span>
              </div>
            </div>
            <div className="shrink-0 flex items-center gap-2 text-xs tabular-nums text-muted-foreground/70">
              {group.displayTime}
              {isOpen ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
            </div>
          </button>
        </CollapsibleTrigger>
        <CollapsibleContent>
          <div className="border-t border-white/[0.04] pl-6">
            {group.rows.map((row) => (
              <UpdateRow key={row.key} row={row} onOpen={onOpen} />
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
  bucketKey,
  rows,
  onOpen,
}: {
  label: string;
  bucketKey: string;
  rows: FeedRow[];
  onOpen: (seriesId: string) => void;
}) {
  const groups = useMemo(() => groupConsecutiveRows(rows, bucketKey), [rows, bucketKey]);
  if (rows.length === 0) return null;
  return (
    <section className="mb-10">
      <div className="px-1 pb-2 text-[11px] uppercase tracking-[0.08em] text-muted-foreground">
        {label}
      </div>
      <div className="rounded-lg overflow-hidden">
        {groups.map((group) =>
          group.type === 'stack' ? (
            <UpdateStack key={group.key} group={group} onOpen={onOpen} />
          ) : (
            <UpdateRow key={group.row.key} row={group.row} onOpen={onOpen} />
          )
        )}
      </div>
    </section>
  );
});

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export default function UpdatesPage() {
  const router = useRouter();
  const { debouncedSearchTerm } = useSearch();
  const search = debouncedSearchTerm.trim().toLowerCase();

  // Each user's library (and its updates) is isolated. Owner-level accounts
  // can flip to every user's updates for support/troubleshooting.
  const { canOwner } = useAuth();
  const [viewAllLibraries, setViewAllLibraries] = useState(false);
  const { data, isLoading } = useUpdatesFeed(0, FETCH_LIMIT, canOwner && viewAllLibraries);
  const [scanning, setScanning] = useState(false);
  // Live scan progress: remaining per-provider chapter checks in the queue.
  // maxSeen anchors the bar's 100% at the largest backlog seen this scan.
  const [scanRemaining, setScanRemaining] = useState(0);
  const scanMaxRef = useRef(0);

  useEffect(() => {
    let cancelled = false;
    let timer: number | undefined;
    const poll = async () => {
      if (cancelled) return;
      try {
        const s = await seriesService.scanStatus();
        if (cancelled) return;
        const remaining = s.waiting + s.running;
        if (remaining > scanMaxRef.current) scanMaxRef.current = remaining;
        if (remaining === 0) scanMaxRef.current = 0;
        setScanRemaining(remaining);
      } catch { /* transient */ }
      // Poll fast while a scan is active, lazily when idle.
      timer = window.setTimeout(() => void poll(), scanMaxRef.current > 0 ? 4000 : 15000);
    };
    void poll();
    return () => { cancelled = true; if (timer) window.clearTimeout(timer); };
  }, []);

  const scanPct = scanMaxRef.current > 0
    ? Math.min(100, Math.round(((scanMaxRef.current - scanRemaining) / scanMaxRef.current) * 100))
    : 0;

  const handleUpdateNow = async () => {
    setScanning(true);
    try {
      await seriesService.scanAllSeries();
      toast.success('Library scan queued — new chapters will appear here as sources are checked.');
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Could not queue the library scan.');
    } finally {
      // Brief lockout so double-clicks don't queue twice while the toast shows.
      setTimeout(() => setScanning(false), 4000);
    }
  };

  const rows = useMemo<FeedRow[]>(() => {
    const items = data ?? [];
    const result: FeedRow[] = [];
    for (let i = 0; i < items.length; i++) {
      const item = items[i];
      if (!item) continue;
      if (search && !item.seriesTitle.toLowerCase().includes(search)) continue;
      const parsed = Date.parse(normalizeUtcString(item.timestamp));
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

  return (
    <div className="mx-auto max-w-[1100px] py-6 sm:py-10">
      {/* Header */}
      <header className="mb-8">
        <div className="flex items-center gap-3">
          <h1 className="text-[22px] font-semibold tracking-tight">Updates</h1>
          <span className="text-sm tabular-nums text-muted-foreground/70">
            {rows.length}
          </span>
          {canOwner && (
            <button
              type="button"
              onClick={() => setViewAllLibraries((v) => !v)}
              aria-pressed={viewAllLibraries}
              title={viewAllLibraries ? "Showing every user's updates — click to view only your own" : "Showing only your updates — click to view every user's"}
              className={`ml-auto inline-flex items-center gap-1.5 rounded-full border px-3 py-1.5 text-xs font-medium transition-colors ${
                viewAllLibraries
                  ? "border-primary/40 bg-primary/15 text-primary"
                  : "border-border/40 bg-foreground/[0.04] text-muted-foreground hover:bg-foreground/[0.06] hover:text-foreground"
              }`}
            >
              {viewAllLibraries ? "All libraries" : "My library"}
            </button>
          )}
          <button
            type="button"
            onClick={() => void handleUpdateNow()}
            disabled={scanning}
            title="Scan every source for new chapters now"
            className={`inline-flex items-center gap-1.5 rounded-full border border-primary/40 bg-primary/10 px-3 py-1.5 text-xs font-medium text-primary transition-colors hover:bg-primary/20 disabled:cursor-not-allowed disabled:opacity-60 ${canOwner ? "" : "ml-auto"}`}
          >
            {scanning ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
            Update now
          </button>
        </div>

        {/* Scan progress — visible while per-provider chapter checks are queued */}
        {scanRemaining > 0 && (
          <div className="mt-4">
            <div className="mb-1.5 flex items-center justify-between text-xs text-muted-foreground">
              <span className="inline-flex items-center gap-1.5">
                <Loader2 className="h-3 w-3 animate-spin text-primary" />
                Checking sources for new chapters…
              </span>
              <span className="tabular-nums">
                {scanRemaining} source check{scanRemaining === 1 ? '' : 's'} remaining · {scanPct}%
              </span>
            </div>
            <div className="h-1.5 w-full overflow-hidden rounded-full bg-foreground/[0.08]">
              <div
                className="h-full rounded-full bg-primary transition-[width] duration-700 ease-out"
                style={{ width: `${Math.max(3, scanPct)}%` }}
              />
            </div>
          </div>
        )}
      </header>

      {/* Body */}
      {isLoading ? (
        <div className="text-center text-xs text-muted-foreground py-16">Loading…</div>
      ) : rows.length === 0 ? (
        <div className="text-center text-xs text-muted-foreground py-16">
          {search
            ? `No updates matching "${debouncedSearchTerm.trim()}".`
            : 'Nothing here yet — new chapters and added series will show up as they arrive.'}
        </div>
      ) : (
        BUCKET_ORDER.map((bucket) => (
          <SectionGroup
            key={bucket}
            label={BUCKET_LABELS[bucket]}
            bucketKey={bucket}
            rows={buckets[bucket]}
            onOpen={handleOpen}
          />
        ))
      )}
    </div>
  );
}
