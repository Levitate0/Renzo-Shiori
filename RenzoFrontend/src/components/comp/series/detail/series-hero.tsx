"use client";

import { useState } from "react";
import { Pause, Play, CheckCircle2, Check, Trash2, FolderOpen, Copy, RefreshCw, MoreHorizontal, Hash } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { useSettings } from "@/lib/api/hooks/useSettings";
import { ResponsiveModal, ResponsiveModalBody } from "@/components/ui/responsive-modal";
import { copyToClipboard } from "@/lib/utils/clipboard";
import { SeriesStatus, type SeriesExtendedInfo } from "@/lib/api/types";
import { formatThumbnailUrl } from "@/lib/utils/thumbnail";
import { CoverLightbox } from "@/components/comp/series/cover-lightbox";
import { FavoriteButton } from "@/components/comp/series/favorite-button";
import { ReadSeriesButton } from "@/components/comp/series/read-series-button";

// Tiny relative-time helper — no external dependency
function formatRelative(dateString: string | null | undefined): string {
  if (!dateString) return '—';
  const normalized = dateString.includes('Z') || dateString.includes('+') || dateString.includes('-', 10)
    ? dateString
    : dateString + 'Z';
  const diff = Date.now() - new Date(normalized).getTime();
  const minutes = Math.floor(diff / 60000);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  if (days < 30) return `${days}d ago`;
  const months = Math.floor(days / 30);
  if (months < 12) return `${months}mo ago`;
  return `${Math.floor(months / 12)}y ago`;
}

interface StatusPillConfig {
  bg: string;
  border: string;
  text: string;
  dot: string;
  pulse: boolean;
}

function getStatusPillConfig(status: SeriesStatus): StatusPillConfig {
  switch (status) {
    case SeriesStatus.ONGOING:
      return {
        bg: 'bg-green-500/12',
        border: 'border-green-500/25',
        text: 'text-green-400',
        dot: 'bg-green-500',
        pulse: true,
      };
    case SeriesStatus.COMPLETED:
      return {
        bg: 'bg-blue-500/12',
        border: 'border-blue-500/25',
        text: 'text-blue-400',
        dot: 'bg-blue-500',
        pulse: false,
      };
    case SeriesStatus.LICENSED:
      return {
        bg: 'bg-purple-500/12',
        border: 'border-purple-500/25',
        text: 'text-purple-400',
        dot: 'bg-purple-500',
        pulse: false,
      };
    case SeriesStatus.PUBLISHING_FINISHED:
      return {
        bg: 'bg-blue-600/12',
        border: 'border-blue-600/25',
        text: 'text-blue-300',
        dot: 'bg-blue-600',
        pulse: false,
      };
    case SeriesStatus.CANCELLED:
      return {
        bg: 'bg-red-500/12',
        border: 'border-red-500/25',
        text: 'text-red-400',
        dot: 'bg-red-500',
        pulse: false,
      };
    case SeriesStatus.ON_HIATUS:
      return {
        bg: 'bg-yellow-500/12',
        border: 'border-yellow-500/25',
        text: 'text-yellow-400',
        dot: 'bg-yellow-500',
        pulse: false,
      };
    case SeriesStatus.DISABLED:
      return {
        bg: 'bg-foreground/[0.06]',
        border: 'border-border/40',
        text: 'text-muted-foreground',
        dot: 'bg-muted-foreground',
        pulse: false,
      };
    default:
      return {
        bg: 'bg-foreground/[0.06]',
        border: 'border-border/40',
        text: 'text-muted-foreground',
        dot: 'bg-muted-foreground',
        pulse: false,
      };
  }
}

function getStatusLabel(status: SeriesStatus): string {
  switch (status) {
    case SeriesStatus.ONGOING: return 'Ongoing';
    case SeriesStatus.COMPLETED: return 'Completed';
    case SeriesStatus.LICENSED: return 'Licensed';
    case SeriesStatus.PUBLISHING_FINISHED: return 'Finished';
    case SeriesStatus.CANCELLED: return 'Cancelled';
    case SeriesStatus.ON_HIATUS: return 'On Hiatus';
    case SeriesStatus.DISABLED: return 'Disabled';
    default: return 'Unknown';
  }
}

export interface SeriesHeroProps {
  series: SeriesExtendedInfo;
  displayTitle: string;
  displayThumbnail: string;
  effectiveStatus: SeriesStatus;
  pausedDownloads: boolean;
  /** Manual 18+ flag (the user-set override, not the tag-derived detection). */
  nsfw: boolean;
  canEditSeries: boolean;
  canDeleteSeries: boolean;
  canManageDownloads: boolean;
  verifyPending: boolean;
  refreshPending: boolean;
  onPauseToggle: () => void;
  onNsfwToggle: () => void;
  onVerify: () => void;
  onRefresh: () => void;
  onDelete: () => void;
  /** Move the series into a category folder (Manga/Manhwa/…); null un-categorizes. */
  onSetCategory?: (category: string | null) => void;
  /** Current state of the hide-decimal-chapters toggle. */
  hideDecimalChapters?: boolean;
  /** Toggle hiding fractional ".5" sub-chapters for this series. */
  onToggleHideDecimal?: () => void;
}

export function SeriesHero({
  series,
  displayTitle,
  displayThumbnail,
  effectiveStatus,
  pausedDownloads,
  nsfw,
  canEditSeries,
  canDeleteSeries,
  canManageDownloads,
  verifyPending,
  refreshPending,
  onPauseToggle,
  onNsfwToggle,
  onVerify,
  onRefresh,
  onDelete,
  onSetCategory,
  hideDecimalChapters,
  onToggleHideDecimal,
}: SeriesHeroProps) {
  const [expanded, setExpanded] = useState(false);
  const [coverExpanded, setCoverExpanded] = useState(false);
  const [copied, setCopied] = useState(false);
  const [categoryDialogOpen, setCategoryDialogOpen] = useState(false);

  const { data: settings } = useSettings();
  const categories = settings?.categorizedFolders ? settings?.categories ?? [] : [];
  // Prefer the category the backend already derived from the storage path
  // (series.category); fall back to parsing the path ({owner}/{Category}/{leaf})
  // only if that field is absent. Matched case-insensitively to a configured one.
  const currentCategory = (() => {
    const fromServer = (series as { category?: string }).category;
    if (fromServer) {
      const m = categories.find((c) => c.toLowerCase() === fromServer.toLowerCase());
      if (m) return m;
    }
    const parts = (series.path ?? series.storagePath ?? "").split("/").filter(Boolean);
    const seg = parts.length >= 3 ? parts[parts.length - 2] : undefined;
    return seg ? categories.find((c) => c.toLowerCase() === seg.toLowerCase()) ?? null : null;
  })();

  const statusConfig = getStatusPillConfig(effectiveStatus);
  const statusLabel = getStatusLabel(effectiveStatus);

  return (
    <section className="relative isolate overflow-hidden border-b border-border/60">
      {/* Blurred banner layer */}
      <div
        aria-hidden
        className="absolute inset-0 -z-10 bg-cover bg-center scale-110 will-change-transform"
        style={{
          backgroundImage: `url(${formatThumbnailUrl(displayThumbnail)})`,
          filter: 'blur(28px) brightness(0.4) saturate(1.4)',
        }}
      />

      {/* Gradient overlay */}
      <div
        aria-hidden
        className="absolute inset-0 -z-10 bg-gradient-to-b from-background/30 via-background/70 to-background"
      />

      {/* Subtle pink tint */}
      <div
        aria-hidden
        className="absolute inset-0 -z-10 bg-[radial-gradient(80%_60%_at_50%_0%,hsl(346.8_77.2%_49.8%/0.10),transparent_70%)]"
      />

      {/* Foreground content */}
      <div className="relative mx-auto max-w-7xl px-4 sm:px-6 py-8 sm:py-10">
        <div className="flex flex-col sm:flex-row gap-6 sm:gap-8">

          {/* Cover — click to expand */}
          <div className="shrink-0 mx-auto sm:mx-0">
            <img
              src={formatThumbnailUrl(displayThumbnail)}
              alt={displayTitle}
              loading="eager"
              style={{ aspectRatio: '4/6' }}
              onClick={() => setCoverExpanded(true)}
              title="Click to expand"
              className="w-[150px] h-[225px] sm:w-[210px] sm:h-[315px] cursor-zoom-in object-cover rounded-xl ring-1 ring-white/[0.06] shadow-[0_30px_60px_-15px_rgba(0,0,0,0.7),0_0_80px_-20px_hsl(346.8_77.2%_49.8%/0.25)] transition-transform hover:scale-[1.02]"
              onError={(e) => {
                const target = e.target as HTMLImageElement;
                if (target.src !== window.location.origin + '/renzo.png') {
                  target.src = '/renzo.png';
                }
              }}
            />
          </div>
          {coverExpanded && (
            <CoverLightbox
              src={formatThumbnailUrl(displayThumbnail)}
              alt={displayTitle}
              onClose={() => setCoverExpanded(false)}
            />
          )}

          {/* Info column */}
          <div className="flex-1 min-w-0 space-y-3 sm:space-y-4">

            {/* Status pill */}
            <div>
              <span
                className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-[11px] font-medium uppercase tracking-wide border ${statusConfig.bg} ${statusConfig.border} ${statusConfig.text}`}
              >
                <span
                  aria-hidden="true"
                  className={`w-1.5 h-1.5 rounded-full ${statusConfig.dot} ${statusConfig.pulse ? 'animate-pulse' : ''}`}
                />
                {statusLabel}
              </span>
            </div>

            {/* Title */}
            <h1 className="text-2xl sm:text-3xl md:text-[34px] font-bold tracking-tight leading-[1.15] text-foreground line-clamp-3">
              {displayTitle}
            </h1>

            {/* Author / Artist row */}
            {(series.author || series.artist) && (
              <p className="text-sm text-muted-foreground">
                by <span className="text-foreground">{series.author}</span>
                {series.artist && series.artist !== series.author && (
                  <> · illust. {series.artist}</>
                )}
              </p>
            )}

            {/* Inline meta row */}
            <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs sm:text-sm text-muted-foreground">
              <span>Ch. {series.chapterList || '—'}</span>
              <span className="opacity-40">·</span>
              <span>{series.chapterCount} chapter{series.chapterCount === 1 ? '' : 's'}</span>
              {series.lastChangeUTC && (
                <>
                  <span className="opacity-40">·</span>
                  <span>Updated {formatRelative(series.lastChangeUTC)}</span>
                </>
              )}
            </div>

            {/* Genre pills */}
            {series.genre && series.genre.length > 0 && (
              <div className="flex flex-wrap gap-1.5">
                {series.genre.map(g => (
                  <span
                    key={g}
                    className="inline-flex items-center rounded-full bg-foreground/[0.06] border border-border/40 px-2 py-0.5 text-[11px] text-foreground/80"
                  >
                    {g}
                  </span>
                ))}
              </div>
            )}

            {/* Description with Read more */}
            {series.description && (
              <div className="max-w-[70ch]">
                <p
                  className={`text-sm text-muted-foreground whitespace-pre-line ${expanded ? '' : 'line-clamp-3'}`}
                >
                  {series.description}
                </p>
                {series.description.length > 240 && (
                  <button
                    onClick={() => setExpanded(v => !v)}
                    className="mt-1 text-xs font-medium text-primary hover:underline"
                  >
                    {expanded ? 'Show less' : 'Read more'}
                  </button>
                )}
              </div>
            )}

            {/* Resume callout — when downloads are paused, the resume action
                gets its own separated, beacon-pulsing area so the user's eye
                lands here first and they're nudged to take action. */}
            {canManageDownloads && pausedDownloads && (
              <div className="flex flex-wrap items-center gap-3 rounded-xl border border-yellow-500/40 bg-yellow-500/10 px-3 py-2.5">
                <span className="flex items-center gap-2 text-sm font-medium text-yellow-700 dark:text-yellow-300">
                  <Pause className="h-4 w-4 shrink-0 fill-current" />
                  Downloads are paused for this series.
                </span>
                <Button
                  onClick={onPauseToggle}
                  className="resume-beacon ml-auto rounded-full bg-yellow-500 font-semibold text-black hover:bg-yellow-400 focus-visible:ring-yellow-400"
                >
                  <Play className="h-4 w-4 mr-2" />
                  Resume Downloads
                </Button>
              </div>
            )}

            {/* Action toolbar */}
            <div className="flex flex-wrap items-center gap-2 pt-1">
              {/* Built-in reader — primary action; resumes where the user left off */}
              <ReadSeriesButton seriesId={series.id} />

              {/* Favourites — personal lists, available to every user level */}
              <FavoriteButton seriesId={series.id} />

              {canManageDownloads && !pausedDownloads && (
                <Button variant="default" onClick={onPauseToggle} className="px-0 w-9 sm:w-auto sm:px-4">
                  <Pause className="h-4 w-4 sm:mr-2" />
                  <span className="hidden sm:inline">Pause Downloads</span>
                </Button>
              )}

              {/* Secondary/admin actions collapse into one menu so the toolbar
                  stays uncluttered — Verify, Refresh, the 18+ flag, and Delete. */}
              {(canEditSeries || canDeleteSeries) && (
                <DropdownMenu>
                  <DropdownMenuTrigger asChild>
                    <Button variant="outline" className="px-0 w-9 sm:w-auto sm:px-4" aria-label="More actions">
                      <MoreHorizontal className="h-4 w-4 sm:mr-2" />
                      <span className="hidden sm:inline">More</span>
                    </Button>
                  </DropdownMenuTrigger>
                  <DropdownMenuContent align="end" className="min-w-[12rem]">
                    {canEditSeries && (
                      <DropdownMenuItem
                        onSelect={(e) => { e.preventDefault(); onRefresh(); }}
                        disabled={refreshPending}
                        className="gap-2"
                      >
                        <RefreshCw className={`h-4 w-4 ${refreshPending ? 'animate-spin' : ''}`} />
                        Refresh metadata &amp; chapters
                      </DropdownMenuItem>
                    )}
                    {canEditSeries && (
                      <DropdownMenuItem
                        onSelect={(e) => { e.preventDefault(); onVerify(); }}
                        disabled={verifyPending}
                        className="gap-2"
                      >
                        {verifyPending ? (
                          <div className="h-4 w-4 animate-spin rounded-full border-2 border-current border-t-transparent" />
                        ) : (
                          <CheckCircle2 className="h-4 w-4" />
                        )}
                        Verify integrity
                      </DropdownMenuItem>
                    )}
                    {canEditSeries && (
                      <DropdownMenuItem onSelect={() => onNsfwToggle()} className="gap-2">
                        <span className={`text-xs font-semibold tracking-tight ${nsfw ? 'text-red-500' : ''}`}>18+</span>
                        {nsfw ? "Unmark as 18+" : "Mark as 18+"}
                      </DropdownMenuItem>
                    )}
                    {canEditSeries && onToggleHideDecimal && (
                      <DropdownMenuItem onSelect={() => onToggleHideDecimal()} className="gap-2">
                        <Hash className={`h-4 w-4 ${hideDecimalChapters ? 'text-primary' : ''}`} />
                        {hideDecimalChapters ? "Show decimal chapters (.5)" : "Hide decimal chapters (.5)"}
                      </DropdownMenuItem>
                    )}
                    {canEditSeries && onSetCategory && categories.length > 0 && (
                      // A dialog rather than a nested hover-submenu — nested Radix
                      // submenus don't open reliably on touch (mobile/webview).
                      <DropdownMenuItem onSelect={() => setCategoryDialogOpen(true)} className="gap-2">
                        <FolderOpen className="h-4 w-4" />
                        Category{currentCategory ? `: ${currentCategory}` : ""}
                      </DropdownMenuItem>
                    )}
                    {canDeleteSeries && (
                      <>
                        {canEditSeries && <DropdownMenuSeparator />}
                        <DropdownMenuItem
                          onSelect={() => onDelete()}
                          className="gap-2 text-destructive focus:text-destructive"
                        >
                          <Trash2 className="h-4 w-4" />
                          Delete series
                        </DropdownMenuItem>
                      </>
                    )}
                  </DropdownMenuContent>
                </DropdownMenu>
              )}
            </div>

            {/* Storage path */}
            {series.path && (
              <div className="flex items-center gap-2 text-[11px] text-muted-foreground/70 font-mono">
                <FolderOpen className="h-3 w-3 shrink-0" />
                <span className="truncate" title={series.path}>{series.path}</span>
                <button
                  onClick={() => {
                    void copyToClipboard(series.path!).then((ok) => {
                      if (ok) {
                        setCopied(true);
                        setTimeout(() => setCopied(false), 1500);
                      }
                    });
                  }}
                  className="inline-flex items-center justify-center h-5 w-5 rounded hover:bg-foreground/10 text-muted-foreground hover:text-foreground transition-colors active:bg-foreground/[0.18] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
                  aria-label="Copy storage path"
                >
                  {copied ? <Check className="h-3 w-3 text-green-500" /> : <Copy className="h-3 w-3" />}
                </button>
              </div>
            )}

          </div>
        </div>
      </div>

      {/* Category picker — touch-friendly dialog (replaces the nested submenu). */}
      {onSetCategory && (
        <ResponsiveModal
          open={categoryDialogOpen}
          onOpenChange={setCategoryDialogOpen}
          title="Category"
          description="Move this series into a category folder."
        >
          <ResponsiveModalBody>
            <div className="flex flex-col gap-1.5 py-1">
              {categories.map((cat) => (
                <Button
                  key={cat}
                  variant={cat === currentCategory ? "default" : "outline"}
                  className="justify-start gap-2"
                  onClick={() => {
                    setCategoryDialogOpen(false);
                    if (cat !== currentCategory) onSetCategory(cat);
                  }}
                >
                  <Check className={`h-4 w-4 ${cat === currentCategory ? "opacity-100" : "opacity-0"}`} />
                  {cat}
                </Button>
              ))}
              {currentCategory && (
                <Button
                  variant="ghost"
                  className="justify-start gap-2 text-muted-foreground"
                  onClick={() => {
                    setCategoryDialogOpen(false);
                    onSetCategory(null);
                  }}
                >
                  <span className="h-4 w-4" />
                  Uncategorized
                </Button>
              )}
            </div>
          </ResponsiveModalBody>
        </ResponsiveModal>
      )}
    </section>
  );
}
