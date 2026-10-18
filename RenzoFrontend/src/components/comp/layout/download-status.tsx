"use client";

import { AlertTriangle, Clock, Download, ExternalLink } from "lucide-react";
import Image from "next/image";
import Link from "next/link";

import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { usePermission } from "@/hooks/use-permission";
import { useDownloadsMetrics } from "@/lib/api/hooks/useDownloads";
import { type DownloadProgress, useDownloadProgress } from "@/lib/api/hooks/useQueue";
import { type DownloadsMetrics, ProgressStatus } from "@/lib/api/types";
import { formatThumbnailUrl } from "@/lib/utils/thumbnail";

/**
 * Download status: the at-a-glance counters (active / queued / failed) and,
 * behind them, the live downloads panel.
 *
 * The panel used to be the Activity Dock, a card floating over the
 * bottom-right corner of every page. With a queue that rarely empties it was
 * almost always there, on top of whatever sat underneath — Install buttons,
 * the last row of a list, Apply bars — and closing it only lasted until the
 * next chapter started. It now drops down from the counters, the way a
 * browser's download button works, and the download icon carries a ring with
 * the running downloads' progress, so the live state stays in view without
 * covering anything.
 *
 * `variant="bar"`    — the counters cluster in the desktop command bar.
 * `variant="icon"`   — one compact button for the mobile command bar, shown
 *                      while something is downloading.
 * `variant="drawer"` — roomier row for the mobile nav drawer footer.
 */
export function DownloadStatus({
  variant = "bar",
}: {
  variant?: "bar" | "icon" | "drawer";
}) {
  const canViewQueue = usePermission("canViewQueue");
  const { data: metrics, isLoading, error } = useDownloadsMetrics();
  const { downloads } = useDownloadProgress();

  if (!canViewQueue || isLoading || error || !metrics) {
    return null;
  }

  const items = [
    {
      label: "Active downloads",
      value: metrics.downloads,
      icon: Download,
      color: "text-blue-500",
    },
    {
      label: "Queued downloads",
      value: metrics.queued,
      icon: Clock,
      color: "text-yellow-500",
    },
    {
      label: "Failed downloads",
      value: metrics.failed,
      icon: AlertTriangle,
      color: "text-red-500",
    },
  ];

  if (variant === "drawer") {
    return (
      <Link
        href="/queue"
        className="flex items-center justify-around rounded-lg border border-border/60 bg-muted/30 px-2 py-2 transition-colors hover:bg-muted/50"
      >
        {items.map(({ label, value, icon: Icon, color }) => (
          <div key={label} className="flex items-center gap-1.5" title={label}>
            <Icon className={`h-4 w-4 ${color}`} />
            <span className={`text-xs font-semibold tabular-nums ${color}`}>
              {value}
            </span>
          </div>
        ))}
      </Link>
    );
  }

  const progress = overallProgress(downloads);
  const summary = `${metrics.downloads} active · ${metrics.queued} queued · ${metrics.failed} failed`;

  if (variant === "icon") {
    if (metrics.downloads === 0 && downloads.length === 0) return null;
    return (
      <DropdownMenu>
        <DropdownMenuTrigger
          aria-label={`Downloads: ${summary}`}
          className="relative flex h-9 w-9 shrink-0 items-center justify-center rounded-lg text-blue-500 outline-none transition-colors hover:bg-accent/60 focus-visible:ring-2 focus-visible:ring-ring/50"
        >
          <DownloadGlyph progress={progress} />
          {metrics.downloads > 0 && (
            <span className="absolute right-0.5 top-0.5 flex h-3.5 min-w-[14px] items-center justify-center rounded-full bg-primary px-0.5 text-[8px] font-bold tabular-nums text-primary-foreground">
              {metrics.downloads > 99 ? "99+" : metrics.downloads}
            </span>
          )}
        </DropdownMenuTrigger>
        <DownloadsPanel downloads={downloads} metrics={metrics} />
      </DropdownMenu>
    );
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        aria-label={`Downloads: ${summary}`}
        title={summary}
        className="flex h-9 items-center gap-2 rounded-lg px-2 outline-none transition-colors hover:bg-accent/60 focus-visible:ring-2 focus-visible:ring-ring/50 data-[state=open]:bg-accent/60"
      >
        {items.map(({ label, value, icon: Icon, color }, i) => (
          <span key={label} className="flex items-center gap-1">
            {i === 0 ? (
              <span className={color}>
                <DownloadGlyph progress={progress} />
              </span>
            ) : (
              <Icon className={`h-4 w-4 ${color}`} />
            )}
            <span className={`text-xs font-semibold tabular-nums ${color}`}>
              {value}
            </span>
          </span>
        ))}
      </DropdownMenuTrigger>
      <DownloadsPanel downloads={downloads} metrics={metrics} />
    </DropdownMenu>
  );
}

/* ─── Panel ───────────────────────────────────────────────────────────── */

function DownloadsPanel({
  downloads,
  metrics,
}: {
  downloads: DownloadProgress[];
  metrics: DownloadsMetrics;
}) {
  // Downloading first, then the ones still starting.
  const sorted = [...downloads].sort(
    (a, b) => rank(a.status) - rank(b.status),
  );

  return (
    <DropdownMenuContent
      align="end"
      sideOffset={8}
      className="w-[min(92vw,380px)] p-0"
    >
      <div className="flex items-center justify-between gap-3 border-b border-border px-3 py-2.5">
        <p className="text-sm font-semibold text-foreground">Downloads</p>
        <div className="flex items-center gap-3 text-xs font-medium tabular-nums">
          <span className="flex items-center gap-1 text-blue-500" title="Active downloads">
            <Download className="h-3.5 w-3.5" />
            {metrics.downloads.toLocaleString()}
          </span>
          <span className="flex items-center gap-1 text-yellow-500" title="Queued downloads">
            <Clock className="h-3.5 w-3.5" />
            {metrics.queued.toLocaleString()}
          </span>
          <span className="flex items-center gap-1 text-red-500" title="Failed downloads">
            <AlertTriangle className="h-3.5 w-3.5" />
            {metrics.failed.toLocaleString()}
          </span>
        </div>
      </div>

      {sorted.length === 0 ? (
        <div className="px-3 py-6 text-center">
          <p className="text-sm text-muted-foreground">Nothing downloading right now.</p>
          {metrics.queued > 0 && (
            <p className="mt-1 text-xs text-muted-foreground/80">
              {metrics.queued.toLocaleString()} chapter{metrics.queued === 1 ? "" : "s"} waiting for their turn.
            </p>
          )}
        </div>
      ) : (
        <div className="max-h-[min(60vh,26rem)] overflow-y-auto p-1">
          {sorted.map((d) => (
            <PanelRow key={d.id} item={d} />
          ))}
        </div>
      )}

      <DropdownMenuItem asChild className="rounded-none border-t border-border bg-muted/30 py-2 focus:bg-muted/60">
        <Link
          href="/queue"
          className="flex cursor-pointer items-center justify-center gap-1 text-xs font-medium text-muted-foreground hover:text-foreground"
        >
          View full queue
          <ExternalLink className="h-3 w-3" />
        </Link>
      </DropdownMenuItem>
    </DropdownMenuContent>
  );
}

function PanelRow({ item }: { item: DownloadProgress }) {
  const card = item.cardInfo;
  const progress = clampPercentage(item.percentage);
  const starting = item.status === ProgressStatus.Started;
  const detail = [card.chapterName, card.provider].filter(Boolean).join(" · ");

  const body = (
    <>
      <div className="relative h-12 w-9 shrink-0 overflow-hidden rounded-md bg-muted">
        {card.thumbnailUrl ? (
          <Image
            src={formatThumbnailUrl(card.thumbnailUrl)}
            alt=""
            fill
            sizes="36px"
            className="object-cover"
            unoptimized
          />
        ) : (
          <div className="flex h-full w-full items-center justify-center text-muted-foreground">
            <Download className="h-4 w-4" />
          </div>
        )}
      </div>
      <div className="flex min-w-0 flex-1 flex-col justify-center">
        <p className="truncate text-xs font-medium text-foreground">
          {card.title || "Download"}
        </p>
        <div className="mt-1 flex items-center gap-2">
          <div className="h-1 flex-1 overflow-hidden rounded-full bg-muted">
            <div
              className="h-full bg-primary transition-[width] duration-300 ease-out"
              style={{ width: `${progress}%` }}
            />
          </div>
          <span className="shrink-0 text-[10px] font-medium tabular-nums text-muted-foreground">
            {starting ? "Starting" : `${progress}%`}
          </span>
        </div>
        {detail && (
          <p className="mt-0.5 truncate text-[10px] text-muted-foreground">
            {detail}
          </p>
        )}
      </div>
    </>
  );

  const rowClass = "flex items-stretch gap-3 rounded-md px-2 py-2";
  if (!card.seriesId) {
    return <div className={rowClass}>{body}</div>;
  }
  return (
    <DropdownMenuItem asChild className={`${rowClass} cursor-pointer`}>
      <Link href={`/library/series?id=${card.seriesId}`}>{body}</Link>
    </DropdownMenuItem>
  );
}

/* ─── Icon ────────────────────────────────────────────────────────────── */

/**
 * The download icon, ringed with the running downloads' average progress
 * while there are any. Same 20px box either way, so the counters beside it
 * don't shift when downloads start or stop.
 */
function DownloadGlyph({ progress }: { progress: number | null }) {
  if (progress === null) {
    return (
      <span className="flex h-5 w-5 items-center justify-center">
        <Download className="h-4 w-4" />
      </span>
    );
  }
  const r = 9;
  const c = 2 * Math.PI * r;
  return (
    <span className="relative flex h-5 w-5 items-center justify-center">
      <svg viewBox="0 0 22 22" className="absolute inset-0 h-full w-full" aria-hidden>
        <circle cx="11" cy="11" r={r} fill="none" stroke="currentColor" strokeOpacity={0.25} strokeWidth={2} />
        <circle
          cx="11"
          cy="11"
          r={r}
          fill="none"
          stroke="currentColor"
          strokeWidth={2}
          strokeLinecap="round"
          strokeDasharray={c}
          strokeDashoffset={c * (1 - progress / 100)}
          transform="rotate(-90 11 11)"
          className="transition-[stroke-dashoffset] duration-300 ease-out"
        />
      </svg>
      <Download className="h-2.5 w-2.5" />
    </span>
  );
}

/* ─── Helpers ─────────────────────────────────────────────────────────── */

function rank(status: ProgressStatus): number {
  return status === ProgressStatus.InProgress ? 0 : 1;
}

/** Average progress of the live downloads, or null when there are none. */
function overallProgress(downloads: DownloadProgress[]): number | null {
  if (downloads.length === 0) return null;
  const total = downloads.reduce((sum, d) => sum + clampPercentage(d.percentage), 0);
  return Math.round(total / downloads.length);
}

function clampPercentage(value: number): number {
  if (!Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(100, Math.round(value)));
}
