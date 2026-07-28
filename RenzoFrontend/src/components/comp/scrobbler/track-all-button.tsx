"use client";

import { useState } from "react";
import { ListChecks } from "lucide-react";
import { toast } from "sonner";
import { useScrobblerConfigs, useAutoMatchAll } from "@/lib/api/hooks/useScrobbler";

/**
 * One-time "Track all" action for the Library top bar: auto-matches the whole
 * library across every connected tracker (MAL/AniList/…). A fire-once button,
 * not a toggle — matches link as they're found. Self-hides when the user has no
 * tracker connected (connect on Account → Trackers first).
 */
export function TrackAllButton() {
  const { data: configs } = useScrobblerConfigs();
  const autoMatchAll = useAutoMatchAll();
  const [busy, setBusy] = useState(false);

  const connected = (configs ?? []).filter((c) => c.isConnected);
  if (connected.length === 0) return null;

  const pending = busy || autoMatchAll.isPending;

  const run = async () => {
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
    <button
      type="button"
      onClick={run}
      disabled={pending}
      title="Match your whole library to your connected trackers"
      className="inline-flex h-8 items-center gap-1.5 whitespace-nowrap rounded-full border border-border/40 bg-foreground/[0.04] px-3 py-1 text-[11px] font-medium text-muted-foreground transition-colors hover:bg-foreground/[0.06] hover:text-foreground disabled:cursor-not-allowed disabled:opacity-60"
    >
      <ListChecks className={`h-3.5 w-3.5 ${pending ? "animate-spin" : ""}`} />
      <span className="hidden sm:inline">Track all</span>
    </button>
  );
}
