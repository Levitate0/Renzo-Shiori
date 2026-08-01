"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { WifiOff } from "lucide-react";
import { getOfflineSeries, type OfflineSeriesView } from "@/lib/native/offline";

function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(0)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1)} MB`;
  return `${(n / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

/**
 * Offline-mode counterpart to <ListSeries> — same card visual language
 * (rounded thumbnail, title bar, chapter count), sourced from the on-device
 * manifest instead of the live library query. Links to /downloads to
 * actually manage/delete a series' saved chapters (in-app offline reading
 * isn't wired up yet — that's separate follow-up work, not part of this
 * toggle).
 */
export function OfflineLibraryGrid() {
  const [series, setSeries] = useState<OfflineSeriesView[] | null>(null);

  useEffect(() => {
    void getOfflineSeries().then(setSeries);
  }, []);

  if (series === null) {
    return <div className="py-12 text-center text-sm text-muted-foreground">Loading offline library…</div>;
  }

  if (series.length === 0) {
    return (
      <div className="flex flex-col items-center gap-2 py-16 text-center text-muted-foreground">
        <WifiOff className="h-8 w-8 opacity-50" />
        <p className="text-sm font-medium">Nothing saved offline yet</p>
        <p className="max-w-xs text-xs">
          Open a series and tap &quot;Save offline&quot; on a chapter to read it without a connection.
        </p>
      </div>
    );
  }

  return (
    <div className="flex flex-wrap gap-2 sm:gap-4">
      {series.map((s) => (
        <Link
          key={s.seriesId}
          href="/downloads"
          className="group w-32 sm:w-45 shrink-0"
          title={`${s.title} — ${s.chapterCount} chapter(s) offline, ${formatBytes(s.bytes)}`}
        >
          <div className="relative aspect-[2/3] w-full overflow-hidden rounded-md bg-muted transition-transform group-hover:scale-105">
            {s.coverSrc ? (
              // eslint-disable-next-line @next/next/no-img-element
              <img src={s.coverSrc} alt={s.title} className="h-full w-full object-cover" />
            ) : (
              <div className="flex h-full w-full items-center justify-center text-muted-foreground">
                <WifiOff className="h-6 w-6 opacity-40" />
              </div>
            )}
            <div className="absolute bottom-0 left-0 flex w-full items-center justify-center rounded-b-md bg-black/60 px-2 py-1 text-xs font-semibold text-white">
              {s.title}
            </div>
          </div>
          <p className="mt-1 truncate text-center text-[11px] text-muted-foreground">
            {s.chapterCount} ch · {formatBytes(s.bytes)}
          </p>
        </Link>
      ))}
    </div>
  );
}
