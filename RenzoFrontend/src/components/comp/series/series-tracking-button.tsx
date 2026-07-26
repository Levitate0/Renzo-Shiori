"use client";

import { useState } from "react";
import { Radio, Check } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { useScrobblerConfigs, useScrobblerMatches } from "@/lib/api/hooks/useScrobbler";
import { SeriesMatchDialog } from "@/components/comp/scrobbler/series-match-dialog";
import { ScrobblerProvider, SeriesMappingStatus } from "@/lib/api/types";

/**
 * Per-series tracking entry point on the series detail toolbar. Lists the
 * trackers the user has connected (MAL, AniList, …) with this series' link
 * status, and opens the existing SeriesMatchDialog to match/confirm/remove a
 * link per provider. Read progress then syncs automatically (push-on-read).
 *
 * Renders nothing when the user has no connected tracker — there's nothing to
 * track to, and the connect flow lives on Account → Trackers.
 */
export function SeriesTrackingButton({ seriesId }: { seriesId: string }) {
  const { data: configs } = useScrobblerConfigs();
  const { data: matches } = useScrobblerMatches();
  const [dialogProvider, setDialogProvider] = useState<ScrobblerProvider | null>(null);

  const connected = (configs ?? []).filter((c) => c.isConnected);
  if (connected.length === 0) return null;

  const linkFor = (p: ScrobblerProvider) =>
    matches?.find(
      (m) =>
        m.seriesId === seriesId &&
        m.provider === p &&
        (m.mappingStatus === SeriesMappingStatus.AutoMatched ||
          m.mappingStatus === SeriesMappingStatus.UserConfirmed),
    );
  const anyLinked = connected.some((c) => linkFor(c.provider));

  return (
    <>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button
            variant="outline"
            className="px-0 w-9 sm:w-auto sm:px-4 gap-2"
            aria-label="Track this series"
          >
            <Radio className="h-4 w-4" />
            <span className="hidden sm:inline">Track</span>
            {anyLinked && <Check className="h-3.5 w-3.5 text-primary" />}
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="w-64">
          <DropdownMenuLabel>Track this series</DropdownMenuLabel>
          <DropdownMenuSeparator />
          {connected.map((c) => {
            const link = linkFor(c.provider);
            return (
              <DropdownMenuItem
                key={c.provider}
                onClick={() => setDialogProvider(c.provider)}
                className="flex items-center justify-between gap-3 cursor-pointer"
              >
                <span className="truncate">{c.displayName}</span>
                {link ? (
                  <span className="flex items-center gap-1 text-xs text-primary shrink-0">
                    <Check className="h-3.5 w-3.5" />
                    {link.externalSeriesTitle ? "Linked" : "Linked"}
                  </span>
                ) : (
                  <span className="text-xs text-muted-foreground shrink-0">Not linked</span>
                )}
              </DropdownMenuItem>
            );
          })}
        </DropdownMenuContent>
      </DropdownMenu>

      {dialogProvider !== null && (
        <SeriesMatchDialog
          seriesId={seriesId}
          provider={dialogProvider}
          open
          onOpenChange={(o) => {
            if (!o) setDialogProvider(null);
          }}
        />
      )}
    </>
  );
}
