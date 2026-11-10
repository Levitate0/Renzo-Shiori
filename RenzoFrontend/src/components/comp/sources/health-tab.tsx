"use client";

import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Loader2 } from "lucide-react";

import { Button } from "@/components/ui/button";
import { sourceHealthService, type SourceHealth } from "@/lib/api/services/sourceHealthService";
import { formatRelativeTime } from "@/components/comp/queue/utils";

/*
 * Sources › Health: how each source your series use is doing, worst first, so
 * a dying source shows before chapters go missing (server:
 * Controllers/SourceHealthController.cs).
 *
 * Hub twin: hub/feature-shiori/src/jvmShared/kotlin/app/renzoshiori/client/ui/sources/HealthTab.kt
 * — same rows, same wording; change one, change both.
 */

export const HEALTH_KEY = ["sources", "health"] as const;

const STATUS: Record<SourceHealth["status"], { label: string; dot: string }> = {
  dead: { label: "Not working", dot: "bg-red-500" },
  failing: { label: "Struggling", dot: "bg-amber-500" },
  ok: { label: "Working", dot: "bg-emerald-500" },
  idle: { label: "No activity", dot: "bg-muted-foreground/40" },
};

/** Hub twin: HealthTab.kt healthDetail. */
export function healthDetail(s: SourceHealth): string {
  const parts: string[] = [];
  parts.push(`${s.enabledCount} of ${s.seriesCount} series on`);
  if (s.steppedDownCount > 0)
    parts.push(`stepped down on ${s.steppedDownCount}${s.reasons.length ? ` (${s.reasons.join(", ")})` : ""}`);
  if (s.failuresLastDay > 0) parts.push(`${s.failuresLastDay} failure${s.failuresLastDay === 1 ? "" : "s"} today`);
  parts.push(s.lastSuccessUtc ? `last worked ${formatRelativeTime(new Date(s.lastSuccessUtc))}` : "no success this week");
  return parts.join(" · ");
}

/** Hub twin: HealthTab.kt healthSpeed. */
export function healthSpeed(s: SourceHealth): string {
  if (s.speed.attempts === 0) return "No downloads this week";
  const rate = `${Math.round((s.speed.successRate ?? 0) * 100)}% of ${s.speed.attempts} downloads this week`;
  return s.speed.medianSeconds == null ? rate : `${rate} · ≈ ${Math.round(s.speed.medianSeconds)} s a chapter`;
}

export function HealthTab({ onMigrate }: { onMigrate: (provider: string) => void }) {
  const { data, isLoading, error } = useQuery({
    queryKey: HEALTH_KEY,
    queryFn: () => sourceHealthService.all(),
    refetchInterval: 60_000,
  });

  if (isLoading)
    return (
      <div className="flex items-center gap-2 py-10 text-sm text-muted-foreground">
        <Loader2 className="h-4 w-4 animate-spin" /> Checking sources…
      </div>
    );
  if (error || !data) return <p className="py-10 text-sm text-destructive">Couldn&apos;t load source health.</p>;
  if (data.length === 0) return <p className="py-10 text-sm text-muted-foreground">None of your series use a source yet.</p>;

  return (
    <div className="space-y-3">
      <p className="text-sm text-muted-foreground">
        How each source your series use has been doing. A source is <em>Not working</em> when it has failed
        on your series all day without serving a single chapter. Failure counts start over when the server restarts.
      </p>
      <div className="divide-y divide-border/50 rounded-lg border border-border/50">
        {data.map((s) => {
          const st = STATUS[s.status];
          return (
            <div key={s.provider} className="flex flex-wrap items-center gap-x-3 gap-y-1 px-3 py-2.5">
              <span className={`h-2 w-2 shrink-0 rounded-full ${st.dot}`} aria-hidden />
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-baseline gap-x-2">
                  <span className="truncate text-sm font-medium">{s.provider}</span>
                  <span className="text-xs text-muted-foreground">{st.label}</span>
                </div>
                <div className="text-xs text-muted-foreground">{healthDetail(s)}</div>
                <div className="text-xs text-muted-foreground/70">{healthSpeed(s)}</div>
              </div>
              {(s.status === "dead" || s.status === "failing") && s.enabledCount > 0 && (
                <Button size="sm" variant="outline" onClick={() => onMigrate(s.provider)}>
                  Migrate
                </Button>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}
