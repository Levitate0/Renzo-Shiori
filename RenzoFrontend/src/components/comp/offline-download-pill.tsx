"use client";
import Link from "next/link";
import { Loader2, DownloadCloud } from "lucide-react";
import { useOfflineDownloadStatus } from "@/lib/native/download-status";

/**
 * Persistent offline-download status. The download runs natively and continues
 * after you leave the series page — this pill (mounted app-wide) shows live
 * progress from anywhere and links to the Downloads page. Renders nothing when
 * no offline download is active (and on the web build, which never emits).
 */
export function OfflineDownloadPill(): React.ReactElement | null {
  const status = useOfflineDownloadStatus();
  const entries = Object.entries(status.series).filter(([, s]) => s.total > 0);
  if (!status.active || entries.length === 0) return null;

  const [, current] = entries[0];
  const pct = current.total ? Math.min(100, Math.round((current.done / current.total) * 100)) : 0;
  const more = entries.length - 1;

  return (
    <Link
      href="/downloads"
      className="fixed inset-x-0 bottom-0 z-[55] mx-auto mb-[calc(env(safe-area-inset-bottom,0px)+12px)] flex w-[min(28rem,calc(100vw-24px))] items-center gap-3 rounded-lg border bg-card/95 px-3 py-2.5 shadow-lg backdrop-blur"
    >
      <DownloadCloud className="h-4 w-4 shrink-0 text-emerald-500" />
      <div className="min-w-0 flex-1">
        <div className="flex items-center justify-between gap-2 text-sm">
          <span className="truncate font-medium">{current.title || "Saving offline…"}</span>
          <span className="shrink-0 tabular-nums text-xs text-muted-foreground">
            {current.done}/{current.total}
          </span>
        </div>
        <div className="mt-1.5 h-1 w-full overflow-hidden rounded-full bg-foreground/10">
          <div className="h-full rounded-full bg-emerald-500 transition-[width]" style={{ width: `${pct}%` }} />
        </div>
        {more > 0 && <div className="mt-1 text-[11px] text-muted-foreground">+{more} more in queue</div>}
      </div>
      <Loader2 className="h-4 w-4 shrink-0 animate-spin text-muted-foreground" />
    </Link>
  );
}
