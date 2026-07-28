"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { Radio, Check, ListChecks, Plug } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  useScrobblerConfigs,
  useScrobblerMatches,
  useAutoMatchSeries,
  useAutoMatchAll,
  useDisableLink,
} from "@/lib/api/hooks/useScrobbler";
import { SeriesMatchDialog } from "@/components/comp/scrobbler/series-match-dialog";
import { ScrobblerProvider, SeriesMappingStatus } from "@/lib/api/types";

/**
 * Per-series tracking control on the series detail toolbar. A single
 * "Track this series" switch (on = tracked to your connected trackers, off =
 * not), plus per-provider rows to fine-tune a specific link via the match
 * dialog. Read progress syncs automatically once a series is linked.
 *
 * Renders nothing when the user has no connected tracker (connect on
 * Account → Trackers first).
 */
export function SeriesTrackingButton({ seriesId }: { seriesId: string }) {
  const router = useRouter();
  const { data: configs } = useScrobblerConfigs();
  const { data: matches } = useScrobblerMatches();
  const autoMatch = useAutoMatchSeries();
  const autoMatchAll = useAutoMatchAll();
  const disableLink = useDisableLink();
  const [dialogProvider, setDialogProvider] = useState<ScrobblerProvider | null>(null);
  const [busy, setBusy] = useState(false);

  const providers = configs ?? [];
  const connected = providers.filter((c) => c.isConnected);
  // Show the control once the user has at least one tracker connected; the list
  // below still shows EVERY tracker (e.g. MAL) so an unconnected one is one tap
  // from connecting rather than being invisible.
  if (connected.length === 0) return null;

  const linkFor = (p: ScrobblerProvider) =>
    matches?.find(
      (m) =>
        m.seriesId === seriesId &&
        m.provider === p &&
        (m.mappingStatus === SeriesMappingStatus.AutoMatched ||
          m.mappingStatus === SeriesMappingStatus.UserConfirmed),
    );
  const activeLinks = connected.filter((c) => linkFor(c.provider));
  const tracked = activeLinks.length > 0;

  const toggleTracking = async (next: boolean) => {
    setBusy(true);
    try {
      if (next) {
        // Attempt to auto-match across the user's connected trackers.
        await autoMatch.mutateAsync(seriesId);
        toast.success("Looking for matches on your trackers…", {
          description: "If nothing was linked, pick a match below.",
        });
      } else {
        // Stop tracking: disable every active link for this series.
        await Promise.all(
          activeLinks.map((c) =>
            disableLink.mutateAsync({ seriesId, provider: c.provider }),
          ),
        );
        toast.success("Stopped tracking this series.");
      }
    } catch {
      toast.error("Couldn't update tracking for this series.");
    } finally {
      setBusy(false);
    }
  };

  // One-time action: auto-match the user's WHOLE library across every connected
  // tracker. Not a toggle — fire it and matches link as they're found.
  const trackAllSeries = async () => {
    setBusy(true);
    try {
      await Promise.all(connected.map((c) => autoMatchAll.mutateAsync(c.provider)));
      toast.success("Tracking all your series…", {
        description: "Matching your library across your connected trackers.",
      });
    } catch {
      toast.error("Couldn't start tracking all series.");
    } finally {
      setBusy(false);
    }
  };

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
            {tracked && <Check className="h-3.5 w-3.5 text-primary" />}
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="w-64">
          {/* Series-level on/off — kept open so the switch is visible */}
          <div
            className="px-2 py-1.5 flex items-center justify-between gap-3"
            onClick={(e) => e.stopPropagation()}
          >
            <span className="text-sm font-medium">Track this series</span>
            <Switch checked={tracked} onCheckedChange={toggleTracking} disabled={busy} />
          </div>
          <DropdownMenuSeparator />
          <DropdownMenuLabel className="text-xs font-normal text-muted-foreground">
            Per tracker — tap to match
          </DropdownMenuLabel>
          {providers.map((c) => {
            const link = linkFor(c.provider);
            return (
              <DropdownMenuItem
                key={c.provider}
                onClick={() =>
                  c.isConnected ? setDialogProvider(c.provider) : router.push("/account")
                }
                className="flex items-center justify-between gap-3 cursor-pointer"
              >
                <span className="truncate">{c.displayName}</span>
                {!c.isConnected ? (
                  <span className="flex items-center gap-1 text-xs text-muted-foreground shrink-0">
                    <Plug className="h-3.5 w-3.5" />
                    Connect
                  </span>
                ) : link ? (
                  <span className="flex items-center gap-1 text-xs text-primary shrink-0">
                    <Check className="h-3.5 w-3.5" />
                    Linked
                  </span>
                ) : (
                  <span className="text-xs text-muted-foreground shrink-0">Not linked</span>
                )}
              </DropdownMenuItem>
            );
          })}

          <DropdownMenuSeparator />
          <DropdownMenuItem
            onClick={trackAllSeries}
            disabled={busy || autoMatchAll.isPending}
            className="flex items-center gap-2 cursor-pointer"
          >
            <ListChecks className={`h-4 w-4 ${autoMatchAll.isPending ? "animate-spin" : ""}`} />
            Track all my series
          </DropdownMenuItem>
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
