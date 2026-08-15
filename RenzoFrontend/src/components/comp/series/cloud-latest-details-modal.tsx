"use client";

import React from 'react';
import {
  Dialog,
  DialogContent,
  DialogTitle,
  DialogDescription,
} from '@/components/ui/dialog';
import {
  Drawer,
  DrawerContent,
  DrawerHeader,
  DrawerTitle,
  DrawerDescription,
  DrawerFooter,
} from '@/components/ui/drawer';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Plus, ExternalLink } from 'lucide-react';
import Image from 'next/image';
import { type LatestSeriesInfo, InLibraryStatus } from '@/lib/api/types';
import { useRouter } from 'next/navigation';
import { BookOpen } from 'lucide-react';
import { useSettings } from '@/lib/api/hooks/useSettings';
import ReactCountryFlag from "react-country-flag";
import { getCountryCodeForLanguage } from "@/lib/utils/language-country-mapping";
import { DynamicTags } from "@/components/comp/series/add-series/steps/confirm-series-step";
import { getStatusDisplay } from "@/lib/utils/series-status";
import { formatThumbnailUrl } from "@/lib/utils/thumbnail";
import { CoverLightbox } from "@/components/comp/series/cover-lightbox";
import { useMediaQuery } from "@/hooks/use-media-query";

interface CloudLatestDetailsModalProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  item: LatestSeriesInfo;
  onAddSeries?: () => void;
}

export const CloudLatestDetailsModal: React.FC<CloudLatestDetailsModalProps> = ({
  open,
  onOpenChange,
  item,
  onAddSeries,
}) => {
  const statusDisplay = getStatusDisplay(item.status);
  const isDesktop = useMediaQuery("(min-width: 768px)");
  const [coverExpanded, setCoverExpanded] = React.useState(false);
  const router = useRouter();
  const { data: settings } = useSettings();
  const readerEnabled = settings?.readerEnabled !== false;

  // Preview reading: pages are fetched live from the source, nothing is stored.
  const handlePreviewRead = () => {
    onOpenChange(false);
    router.push(`/reader?preview=1&mihonId=${encodeURIComponent(item.mihonId)}&chapter=-1&title=${encodeURIComponent(item.title)}${item.seriesId ? `&seriesId=${item.seriesId}` : ""}`);
  };

  const handleViewSource = () => {
    if (item.url) {
      window.open(item.url, '_blank', 'noopener,noreferrer');
    }
  };

  const handleAddSeries = () => {
    onOpenChange(false);
    onAddSeries?.();
  };

  const formatUpdatedDate = () => {
    if (!item.fetchDate) return null;
    const date = new Date(item.fetchDate);
    const month = date.toLocaleString('en-US', { month: 'short' });
    const year = date.getFullYear();
    return `Updated ${month} ${year}`;
  };

  const sourceBadge = (clickable: boolean) => (
    <span
      className={`inline-flex items-center gap-1 bg-accent text-accent-foreground rounded px-2 py-0.5 text-sm font-medium border border-border max-w-full ${
        clickable ? "cursor-pointer hover:bg-accent/80 transition-colors" : ""
      }`}
      onClick={clickable ? handleViewSource : undefined}
      title={clickable ? "Click to open in the source" : undefined}
    >
      <ReactCountryFlag
        countryCode={getCountryCodeForLanguage(item.language)}
        svg
        className="shrink-0"
        style={{
          width: '16px',
          height: '12px',
          borderRadius: '2px',
          border: '1px solid #ccc',
        }}
        title={item.language.toUpperCase()}
      />
      <span className="truncate">{item.provider}</span>
      {clickable && <ExternalLink className="h-3 w-3 shrink-0" />}
    </span>
  );

  const byline = () => {
    const parts: string[] = [];
    if (item.author) parts.push(`by ${item.author}`);
    if (item.artist && item.artist !== item.author) parts.push(`art by ${item.artist}`);
    return parts.length > 0 ? parts.join(" · ") : null;
  };

  if (isDesktop) {
    return (
      <Dialog open={open} onOpenChange={onOpenChange}>
        {/* Half the viewport, not a fixed 660px. On a wide monitor that cap left
            the card occupying about a quarter of the screen while the synopsis —
            the thing the card exists to show — wrapped into a narrow column.
            Grows in BOTH dimensions: the old card was 660x373 (cover, badge and
            padding over a 61px footer), near enough 16:9, so holding that ratio
            keeps the same shape instead of stretching it into a letterbox. The
            floor keeps it from shrinking on small laptops, where 50vw is narrower
            than the old fixed width; the height cap keeps it on screen when the
            display is wide but short. */}
        <DialogContent className="w-[95vw] md:w-[50vw] md:max-w-none md:min-w-[660px] md:aspect-[16/9] md:max-h-[85vh] p-0 overflow-hidden flex flex-col">
          <DialogTitle className="sr-only">{item.title}</DialogTitle>
          <DialogDescription className="sr-only">
            Details for {item.title}
          </DialogDescription>

          {coverExpanded && (
            <CoverLightbox src={formatThumbnailUrl(item.thumbnailUrl)} alt={item.title} onClose={() => setCoverExpanded(false)} />
          )}
          {/* Content area — takes the height the ratio adds, so the footer stays
              pinned at the bottom instead of the card ending early with dead space
              beneath it. Scrolls internally if a long synopsis outgrows it. */}
          <div className="p-5 flex gap-0 items-start flex-1 min-h-0 overflow-y-auto">
            {/* Cover wrap */}
            {/* A fixed pixel width can't hold its proportion against a card that is
                now a share of the viewport — it shrank to a thumbnail on a wide
                monitor and dominated on a narrow one. Pinned to 26% of the card
                instead, matching the Hub's proportions, so the cover reads the same
                at any width. */}
            <div className="shrink-0 w-[26%]">
              <div
                className="w-full aspect-[2/3] cursor-zoom-in rounded-xl overflow-hidden bg-muted border border-border shadow-md relative"
                title="Click to expand"
                onClick={() => setCoverExpanded(true)}
              >
                <Image
                  src={formatThumbnailUrl(item.thumbnailUrl)}
                  alt={item.title}
                  fill
                  className="object-cover"
                  onError={(e) => {
                    const target = e.target as HTMLImageElement;
                    if (target.src !== window.location.origin + '/renzo.png') {
                      target.src = '/renzo.png';
                    }
                  }}
                />
              </div>
              {/* Source badge below cover */}
              <div className="mt-2 w-full flex justify-center">
                {item.url ? sourceBadge(true) : sourceBadge(false)}
              </div>
            </div>

            {/* Metadata */}
            <div className="flex-1 min-w-0 pl-[18px]">
              {/* Title + status */}
              <div className="flex items-center gap-2 flex-wrap mb-1">
                <span className="text-[17px] font-bold tracking-tight leading-tight">
                  {item.title}
                </span>
                <Badge className={`text-xs shrink-0 ${statusDisplay.color}`}>
                  {statusDisplay.text}
                </Badge>
              </div>

              {/* Byline */}
              {byline() && (
                <div className="text-[12.5px] text-muted-foreground mb-2">
                  {byline()}
                </div>
              )}

              {/* Genre tags */}
              {item.genre && item.genre.length > 0 && (
                <div className="flex flex-wrap gap-[5px] mt-2">
                  {item.genre.map((g) => (
                    <span
                      key={g}
                      className="inline-flex items-center px-2 py-0.5 rounded-full bg-muted border border-border text-[11px] text-muted-foreground"
                    >
                      {g}
                    </span>
                  ))}
                </div>
              )}

              {/* Description */}
              {/* The clamp existed because the old card was short. With the taller
                  one it would leave the synopsis truncated above empty space, so
                  it relaxes where there is room to relax into. */}
              <p className="text-[12.5px] text-muted-foreground leading-relaxed mt-2.5 line-clamp-4 md:line-clamp-[10] lg:line-clamp-none">
                {item.description || "No description available"}
              </p>

              {/* Chapter count + date */}
              <div className="mt-3 text-[11.5px] text-muted-foreground/60">
                {(item.chapterCount ?? item.latestChapter) != null && (
                  <span>{item.chapterCount ?? item.latestChapter} chapters</span>
                )}
                {(item.chapterCount ?? item.latestChapter) != null && formatUpdatedDate() && " · "}
                {formatUpdatedDate() && <span>{formatUpdatedDate()}</span>}
              </div>
            </div>
          </div>

          {/* Footer */}
          <div className="px-5 py-3 border-t border-border flex items-center justify-between bg-card/50">
            <div className="text-[11.5px] text-muted-foreground/60">
              {item.provider && <span>{item.provider}</span>}
            </div>
            <div className="flex gap-2">
              {item.url && (
                <Button
                  variant="outline"
                  className="gap-1"
                  onClick={handleViewSource}
                >
                  <ExternalLink className="h-4 w-4" />
                  View Source
                </Button>
              )}
              {readerEnabled && (
                <Button variant="outline" className="gap-1" onClick={handlePreviewRead}>
                  <BookOpen className="h-4 w-4" />
                  Read
                </Button>
              )}
              {item.inLibrary === InLibraryStatus.NotInLibrary && onAddSeries && (
                <Button
                  className="gap-1"
                  onClick={handleAddSeries}
                >
                  <Plus className="h-4 w-4" />
                  Add to Library
                </Button>
              )}
            </div>
          </div>
        </DialogContent>
      </Dialog>
    );
  }

  return (
    <Drawer open={open} onOpenChange={onOpenChange}>
      {/* Cap the drawer to the viewport so a long description can't push the
          footer (Add / Read / View Source) off-screen — the body below scrolls. */}
      <DrawerContent className="max-h-[90dvh]">
        <DrawerHeader className="sr-only">
          <DrawerTitle>{item.title}</DrawerTitle>
          <DrawerDescription>Details for {item.title}</DrawerDescription>
        </DrawerHeader>

        {coverExpanded && (
          <CoverLightbox src={formatThumbnailUrl(item.thumbnailUrl)} alt={item.title} onClose={() => setCoverExpanded(false)} />
        )}
        {/* Scrollable body — min-h-0 lets this flex child shrink below its
            content so it actually scrolls (instead of pushing the footer off). */}
        <div className="overflow-y-auto flex-1 min-h-0" data-vaul-no-drag>
          {/* Cover section */}
          <div className="flex flex-col items-center px-5 py-3 border-b border-border">
            <div
              className="w-[130px] aspect-[2/3] cursor-zoom-in rounded-xl overflow-hidden bg-muted border border-border shadow-md relative mb-2.5"
              title="Click to expand"
              onClick={() => setCoverExpanded(true)}
            >
              <Image
                src={formatThumbnailUrl(item.thumbnailUrl)}
                alt={item.title}
                fill
                className="object-cover"
                onError={(e) => {
                  const target = e.target as HTMLImageElement;
                  if (target.src !== window.location.origin + '/renzo.png') {
                    target.src = '/renzo.png';
                  }
                }}
              />
            </div>
            <div className="text-center text-sm font-bold tracking-tight leading-tight mb-1">
              {item.title}
            </div>
            {byline() && (
              <div className="text-center text-[11px] text-muted-foreground mb-1.5">
                {byline()}
              </div>
            )}
            <div className="flex justify-center gap-1">
              <Badge className={`text-xs ${statusDisplay.color}`}>
                {statusDisplay.text}
              </Badge>
              {(item.chapterCount ?? item.latestChapter) != null && (
                <Badge variant="secondary" className="text-xs">
                  {item.chapterCount ?? item.latestChapter} chapters
                </Badge>
              )}
            </div>
          </div>

          {/* Tags section */}
          {item.genre && item.genre.length > 0 && (
            <div className="px-3.5 py-2.5 border-b border-border">
              <div className="flex flex-wrap gap-1">
                {item.genre.map((g) => (
                  <span
                    key={g}
                    className="inline-flex items-center px-2 py-0.5 rounded-full bg-muted border border-border text-[10px] text-muted-foreground"
                  >
                    {g}
                  </span>
                ))}
              </div>
            </div>
          )}

          {/* Description section */}
          <div className="px-3.5 py-2.5 border-b border-border">
            <p className="text-[11.5px] text-muted-foreground leading-relaxed">
              {item.description || "No description available"}
            </p>
          </div>

          {/* Source badges section */}
          <div className="px-3.5 py-2 border-b border-border flex gap-[5px] flex-wrap">
            {item.url ? sourceBadge(true) : sourceBadge(false)}
          </div>
        </div>

        {/* Footer */}
        <DrawerFooter className="flex-col gap-1.5 pb-[max(1rem,env(safe-area-inset-bottom))]">
          {item.inLibrary === InLibraryStatus.NotInLibrary && onAddSeries && (
            <Button
              className="w-full gap-1"
              onClick={handleAddSeries}
            >
              <Plus className="h-4 w-4" />
              Add to Library
            </Button>
          )}
          {readerEnabled && (
            <Button variant="outline" className="w-full gap-1" onClick={handlePreviewRead}>
              <BookOpen className="h-4 w-4" />
              Read
            </Button>
          )}
          {item.url && (
            <Button
              variant="outline"
              className="w-full gap-1"
              onClick={handleViewSource}
            >
              <ExternalLink className="h-4 w-4" />
              View Source
            </Button>
          )}
        </DrawerFooter>
      </DrawerContent>
    </Drawer>
  );
};
