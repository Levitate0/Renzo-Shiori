"use client";

import {
  AlertTriangle,
  Bookmark,
  Check,
  CheckCircle2,
  CheckSquare,
  ChevronDown,
  Circle,
  Download,
  Loader2,
  Lock,
  RefreshCw,
  Square,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { normalizeUtcString } from "@/components/comp/queue/utils";
import { type ChapterDetail } from "@/lib/api/types";
import { cn } from "@/lib/utils";

export interface ChapterRowProps {
  chapter: ChapterDetail;
  /** Series is paused — the action is blocked. */
  paused: boolean;
  /** User may queue downloads. When false the action column is hidden. */
  canManage: boolean;
  /** This chapter currently has a queued re-download in flight. */
  isPending: boolean;
  /** Omit providerId for the priority default; pass it to force a specific source. */
  onRedownload: (chapterNumber: number, providerId?: string) => void;
  /** Built-in reader: open this chapter. Undefined when the reader is disabled. */
  onRead?: (chapterNumber: number) => void;
  /** Toggle read/unread without opening the reader. Undefined when reader is disabled. */
  onToggleRead?: (chapterNumber: number, read: boolean) => void;
  /** A read/unread toggle for this chapter is in flight. */
  readPending?: boolean;
  /** Read-state overlay from the reader API (progress 0..1). */
  readProgress?: number;
  readCompleted?: boolean;
  readBookmarked?: boolean;
  /** Multi-select mode is active — the row selects instead of opening the reader. */
  selecting?: boolean;
  /** This chapter is in the current selection. */
  selected?: boolean;
  /** Toggle this chapter's selection. shiftKey requests a range from the last click. */
  onSelectToggle?: (chapterNumber: number, shiftKey: boolean) => void;
}

function formatChapter(n: number | undefined): string {
  if (n == null) return "—";
  return `Ch. ${n}`;
}

/**
 * Upload date as shown on the row: a "N days ago" relative label within the last
 * week (the freshness window people care about), an absolute date beyond that.
 */
function formatUpload(iso: string | null | undefined): string | null {
  if (!iso) return null;
  // The backend serializes UTC without a zone marker — normalize so the browser
  // doesn't parse it as local time (which would skew "N days ago" by timezone).
  const d = new Date(normalizeUtcString(iso));
  if (Number.isNaN(d.getTime())) return null;
  const days = Math.floor((Date.now() - d.getTime()) / 86_400_000);
  if (days < 0) return d.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
  if (days === 0) return "Today";
  if (days === 1) return "Yesterday";
  if (days < 7) return `${days} days ago`;
  return d.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
}

export function ChapterRow({
  chapter,
  paused,
  canManage,
  isPending,
  onRedownload,
  onRead,
  onToggleRead,
  readPending,
  readProgress,
  readCompleted,
  readBookmarked,
  selecting,
  selected,
  onSelectToggle,
}: ChapterRowProps) {
  const num = chapter.number;
  const label = chapter.downloaded ? "Re-download" : "Download";
  const uploadLabel = formatUpload(chapter.uploadDate);
  // The purchase action lives in the reader's locked screen (no exit/re-enter to
  // unlock); the list only marks the chapter as locked to keep rows uncluttered.
  const locked = !!chapter.locked;

  const disabledReason = paused
    ? "Unpause the series to re-download"
    : chapter.availableProviders.length === 0
      ? "No source available to download this chapter"
      : num == null
        ? "Chapter number is unknown"
        : null;

  // Suwayomi-style: clicking anywhere on the row body opens the chapter. Works
  // for not-yet-downloaded chapters too — the reader streams those live from the
  // source. Action buttons stop the click from bubbling so they keep their own
  // behaviour. In multi-select mode the row toggles selection instead.
  const canSelect = selecting && onSelectToggle && num != null;
  // Locked chapters still open the reader — that's where the purchase button lives.
  const openReader = onRead && num != null ? () => onRead(num) : undefined;
  const clickable = canSelect || !!openReader;

  const handleRowClick = (e: React.MouseEvent | React.KeyboardEvent) => {
    if (canSelect) onSelectToggle!(num!, "shiftKey" in e ? e.shiftKey : false);
    else openReader?.();
  };

  return (
    <div
      onClick={clickable ? handleRowClick : undefined}
      role={clickable ? "button" : undefined}
      tabIndex={clickable ? 0 : undefined}
      onKeyDown={
        clickable
          ? (e) => {
              if (e.key === "Enter" || e.key === " ") {
                e.preventDefault();
                handleRowClick(e);
              }
            }
          : undefined
      }
      aria-pressed={canSelect ? !!selected : undefined}
      className={cn(
        "flex items-center gap-3 rounded-lg border border-border/40 bg-card/50 px-3 py-2.5",
        "transition-colors hover:bg-foreground/[0.03]",
        clickable && "cursor-pointer",
        selected && "border-primary/50 bg-primary/10 hover:bg-primary/15"
      )}
    >
      {/* Selection checkbox — only in multi-select mode */}
      {selecting && (
        selected ? (
          <CheckSquare className="h-4 w-4 shrink-0 text-primary" />
        ) : (
          <Square className="h-4 w-4 shrink-0 text-muted-foreground/50" />
        )
      )}

      {/* Status icon */}
      {chapter.downloaded ? (
        <CheckCircle2 className="h-4 w-4 shrink-0 text-emerald-500" />
      ) : locked ? (
        <Lock className="h-4 w-4 shrink-0 text-violet-400" />
      ) : (
        <AlertTriangle className="h-4 w-4 shrink-0 text-amber-500" />
      )}

      {/* Chapter number + title */}
      <div className="min-w-0 flex-1">
        <div className="flex items-baseline gap-2">
          <span className={cn("text-sm font-medium tabular-nums", readCompleted && "text-muted-foreground/60")}>
            {formatChapter(num)}
          </span>
          {chapter.name && (
            <span
              className={cn("truncate text-sm text-muted-foreground", readCompleted && "text-muted-foreground/50")}
              title={chapter.name}
            >
              {chapter.name}
            </span>
          )}
          {readBookmarked && <Bookmark className="h-3 w-3 shrink-0 self-center fill-pink-500 text-pink-500" />}
          {!readCompleted && (readProgress ?? 0) > 0 && (
            <span className="shrink-0 self-center rounded bg-primary/15 px-1 text-[10px] font-medium text-primary">
              {Math.round((readProgress ?? 0) * 100)}%
            </span>
          )}
        </div>
        <div className="mt-0.5 text-[11px]">
          {chapter.downloaded ? (
            <span className="text-muted-foreground">
              from{" "}
              <span className="font-medium text-foreground/80">
                {chapter.sourceProviderName ?? "unknown source"}
              </span>
            </span>
          ) : locked ? (
            <span className="inline-flex items-center gap-1 font-medium text-violet-400">
              Locked · purchase on source
            </span>
          ) : (
            <span className="inline-flex items-center gap-1 font-medium text-amber-500">
              Missing
            </span>
          )}
          {uploadLabel && (
            <span className="text-muted-foreground/70">
              {" · "}
              <span title={chapter.uploadDate ? new Date(normalizeUtcString(chapter.uploadDate)).toLocaleString() : undefined}>
                {uploadLabel}
              </span>
            </span>
          )}
        </div>
      </div>

      {/* Action cluster — clicks here must not trigger the row-open. Hidden in
          multi-select mode so the whole row is a selection target. */}
      {!selecting && (
      <div className="flex items-center gap-2 shrink-0" onClick={(e) => e.stopPropagation()}>
      {/* Read/unread toggle — marks the chapter without opening the reader */}
      {onToggleRead && num != null && (
        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              size="icon"
              variant="ghost"
              disabled={readPending}
              onClick={() => onToggleRead(num, !readCompleted)}
              aria-pressed={readCompleted}
              aria-label={readCompleted ? "Mark as unread" : "Mark as read"}
              className="h-8 w-8 shrink-0"
            >
              {readPending ? (
                <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" />
              ) : readCompleted ? (
                <CheckCircle2 className="h-4 w-4 text-emerald-500" />
              ) : (
                <Circle className="h-4 w-4 text-muted-foreground/50" />
              )}
            </Button>
          </TooltipTrigger>
          <TooltipContent>{readCompleted ? "Mark as unread" : "Mark as read"}</TooltipContent>
        </Tooltip>
      )}

      {/* Re-download split button */}
      {canManage && !locked && (
        <div className="shrink-0">
          {disabledReason ? (
            <Tooltip>
              <TooltipTrigger asChild>
                <span tabIndex={0} className="inline-flex">
                  <Button size="sm" variant="outline" disabled className="h-8 gap-1.5">
                    {chapter.downloaded ? (
                      <RefreshCw className="h-3.5 w-3.5" />
                    ) : (
                      <Download className="h-3.5 w-3.5" />
                    )}
                    {label}
                  </Button>
                </span>
              </TooltipTrigger>
              <TooltipContent>{disabledReason}</TooltipContent>
            </Tooltip>
          ) : (
            <div className="inline-flex items-stretch">
              <Button
                size="sm"
                variant="outline"
                disabled={isPending}
                onClick={() => num != null && onRedownload(num)}
                className="h-8 gap-1.5 rounded-r-none border-r-0"
                title={
                  chapter.downloaded
                    ? `Re-download from the best source`
                    : `Download chapter ${num}`
                }
              >
                {isPending ? (
                  <Loader2 className="h-3.5 w-3.5 animate-spin" />
                ) : chapter.downloaded ? (
                  <RefreshCw className="h-3.5 w-3.5" />
                ) : (
                  <Download className="h-3.5 w-3.5" />
                )}
                {label}
              </Button>
              <DropdownMenu>
                <DropdownMenuTrigger asChild>
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={isPending}
                    className="h-8 rounded-l-none px-1.5"
                    aria-label="Choose source"
                  >
                    <ChevronDown className="h-3.5 w-3.5" />
                  </Button>
                </DropdownMenuTrigger>
                <DropdownMenuContent align="end" className="min-w-[11rem]">
                  <DropdownMenuLabel className="text-xs text-muted-foreground">
                    {chapter.downloaded ? "Re-download from" : "Download from"}
                  </DropdownMenuLabel>
                  <DropdownMenuSeparator />
                  {chapter.availableProviders.map((src) => (
                    <DropdownMenuItem
                      key={src.id}
                      onSelect={() => num != null && onRedownload(num, src.id)}
                      className="gap-2"
                    >
                      <span className="flex-1 truncate">{src.name}</span>
                      {src.id === chapter.sourceProviderId && (
                        <Check className="h-3.5 w-3.5 text-muted-foreground" />
                      )}
                    </DropdownMenuItem>
                  ))}
                </DropdownMenuContent>
              </DropdownMenu>
            </div>
          )}
        </div>
      )}
      </div>
      )}
    </div>
  );
}
