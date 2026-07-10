"use client";

import React, { memo, useMemo } from 'react';
import Image from 'next/image';
import { useRouter } from 'next/navigation';
import { BookPlus, BookOpen } from 'lucide-react';
import { useUpdatesFeed } from '@/lib/api/hooks/useSeries';
import { useSearch } from '@/contexts/search-context';
import { formatThumbnailUrl } from '@/lib/utils/thumbnail';
import { type UpdateFeedItem } from '@/lib/api/types';
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

const FETCH_LIMIT = 500;

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
            (e.target as HTMLImageElement).src = '/rensaio.png';
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
      <div className="shrink-0 text-xs tabular-nums text-muted-foreground/70">
        {row.displayTime}
      </div>
    </button>
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
        {rows.map((row) => (
          <UpdateRow key={row.key} row={row} onOpen={onOpen} />
        ))}
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

  const { data, isLoading } = useUpdatesFeed(0, FETCH_LIMIT);

  const rows = useMemo<FeedRow[]>(() => {
    const items = data ?? [];
    const result: FeedRow[] = [];
    for (let i = 0; i < items.length; i++) {
      const item = items[i];
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
        <div className="flex items-baseline gap-3">
          <h1 className="text-[22px] font-semibold tracking-tight">Updates</h1>
          <span className="text-sm tabular-nums text-muted-foreground/70">
            {rows.length}
          </span>
        </div>
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
            rows={buckets[bucket]}
            onOpen={handleOpen}
          />
        ))
      )}
    </div>
  );
}
