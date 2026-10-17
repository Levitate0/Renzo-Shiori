"use client";

import React from "react";
import { Cpu, Download, Info, RotateCcw } from "lucide-react";
import type { SystemHealth } from "@/lib/api/types";

/**
 * Owner-only "Engine" panel on the Status page: the extension engine (the JVM
 * sidecar every source runs in) and the download queue, live. Everything here
 * used to be visible only in the container logs, which is where "why isn't
 * anything downloading?" always ended up being answered.
 *
 * Mirrored in the Hub's StatusScreen (EnginePanel.kt); change both.
 */

function ago(iso?: string | null): string | null {
  if (!iso) return null;
  const s = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (s < 60) return `${s}s ago`;
  const m = Math.round(s / 60);
  if (m < 60) return `${m} min ago`;
  const h = Math.round(m / 60);
  if (h < 48) return `${h} h ago`;
  return `${Math.round(h / 24)} days ago`;
}

function duration(minutes: number): string {
  if (minutes < 60) return `${minutes} minutes`;
  const h = minutes / 60;
  if (h < 48) return `${Math.round(h)} hours`;
  return `${Math.round(h / 24)} days`;
}

/** What, in plain words, explains the numbers — or null when nothing does. */
export function diagnose(h: SystemHealth): { tone: "ok" | "warn" | "bad"; text: string }[] {
  const out: { tone: "ok" | "warn" | "bad"; text: string }[] = [];
  const e = h.engine;
  const d = h.downloads;
  if (e && !e.running) {
    out.push({ tone: "bad", text: "The extension engine is not running, so no source can be reached. It restarts on its own; if this persists, check the server logs." });
  } else if (e && !e.responding) {
    out.push({ tone: "bad", text: "The extension engine is not answering. If it stays this way for a minute it is restarted automatically." });
  } else if (e && e.workers != null && e.busyWorkers != null && e.busyWorkers >= e.workers && (e.lastCompletedSecondsAgo ?? 0) > 60) {
    out.push({ tone: "warn", text: `Every engine worker is busy and nothing has finished for ${e.lastCompletedSecondsAgo}s. Usually a slow or Cloudflare-protected source; it clears by itself.` });
  }
  if (!d.autoDownloadNewChapters) {
    out.push({ tone: "warn", text: "Auto-download of new chapters is off in Settings — chapter lists update, but nothing new is queued." });
  }
  if (d.waitingDue > 0 && d.slotsInUse >= d.maxSlots) {
    out.push({
      tone: "warn",
      text: `All ${d.maxSlots} download slot${d.maxSlots === 1 ? " is" : "s are"} in use with ${d.waitingDue.toLocaleString()} chapter${d.waitingDue === 1 ? "" : "s"} waiting. Raising “Simultaneous downloads” in Settings drains this faster.`,
    });
  }
  const giveUpMinutes = d.retries * d.retryEveryMinutes;
  if (giveUpMinutes >= 24 * 60) {
    out.push({
      tone: "warn",
      text: `A chapter that keeps failing on one source is retried there for about ${duration(giveUpMinutes)} (${d.retries} × ${d.retryEveryMinutes} min) before the next source is tried. Fewer retries means faster fallback.`,
    });
  }
  if (out.length === 0) out.push({ tone: "ok", text: "Everything is running normally." });
  return out;
}

function Row({ label, value, hint }: { label: string; value: React.ReactNode; hint?: string }) {
  return (
    <div className="flex items-baseline justify-between gap-3 py-1" title={hint}>
      <span className="text-[12px] text-muted-foreground">{label}</span>
      <span className="font-mono text-[12.5px] tabular-nums text-foreground">{value}</span>
    </div>
  );
}

function Card({ title, icon, children }: { title: string; icon: React.ReactNode; children: React.ReactNode }) {
  return (
    <div className="rounded-xl border border-white/[0.06] bg-white/[0.02] p-4">
      <div className="mb-2 flex items-center gap-2">
        <span className="text-muted-foreground">{icon}</span>
        <span className="text-[10.5px] font-medium uppercase tracking-[0.16em] text-muted-foreground">{title}</span>
      </div>
      {children}
    </div>
  );
}

function Meter({ used, max }: { used: number; max: number }) {
  const pct = max > 0 ? Math.min(100, Math.round((used / max) * 100)) : 0;
  return (
    <div className="mt-1 h-1.5 w-full overflow-hidden rounded-full bg-white/[0.06]">
      <div
        className={`h-full rounded-full transition-[width] duration-500 ${pct >= 100 ? "bg-amber-400" : "bg-primary"}`}
        style={{ width: `${pct}%` }}
      />
    </div>
  );
}

export function EnginePanel({ health }: { health: SystemHealth }) {
  const e = health.engine;
  const d = health.downloads;
  const notes = diagnose(health);
  const toneClass = { ok: "text-emerald-400", warn: "text-amber-400", bad: "text-red-400" } as const;

  return (
    <div className="space-y-3">
      <div className="grid gap-3 md:grid-cols-3">
        <Card title="Extension engine" icon={<Cpu className="h-4 w-4" />}>
          {e ? (
            <>
              <Row
                label="State"
                value={
                  <span className={!e.running || !e.responding ? "text-red-400" : "text-emerald-400"}>
                    {!e.running ? "Stopped" : e.responding ? "Running" : "Not answering"}
                  </span>
                }
              />
              {e.workers != null && e.busyWorkers != null && (
                <>
                  <Row label="Busy workers" value={`${e.busyWorkers} / ${e.workers}`} hint="Source calls in progress" />
                  <Meter used={e.busyWorkers} max={e.workers} />
                </>
              )}
              {e.heapUsedMb != null && e.heapMaxMb != null && (
                <Row label="Memory" value={`${e.heapUsedMb} / ${e.heapMaxMb} MB`} />
              )}
              <Row
                label="Browser helpers"
                value={`${e.browserHelpers} / ${e.browserHelperLimit}`}
                hint="Chromium processes used for Cloudflare and WebView sources; the engine is recycled at the limit"
              />
              {e.startedAt && <Row label="Up since" value={ago(e.startedAt)} />}
            </>
          ) : (
            <p className="text-[12px] text-muted-foreground">Not available on this server.</p>
          )}
        </Card>

        <Card title="Downloads" icon={<Download className="h-4 w-4" />}>
          <Row label="Slots in use" value={`${d.slotsInUse} / ${d.maxSlots}`} hint={`At most ${d.perSource} per source`} />
          <Meter used={d.slotsInUse} max={d.maxSlots} />
          <Row label="Waiting now" value={d.waitingDue.toLocaleString()} hint="Due to run as soon as a slot frees" />
          <Row label="Scheduled later" value={d.waitingLater.toLocaleString()} hint="Retries waiting out their back-off" />
          <Row label="Failed" value={d.failed.toLocaleString()} />
          <Row label="Per source" value={d.perSource} />
        </Card>

        <Card title="Restarts" icon={<RotateCcw className="h-4 w-4" />}>
          {e ? (
            <>
              <Row label="Since server start" value={e.restartsSinceAppStart} />
              <Row label="Last" value={ago(e.lastRestartAt) ?? "never"} />
              {e.lastRestartReason && (
                <p className="mt-1 text-[11.5px] leading-snug text-muted-foreground">{e.lastRestartReason}</p>
              )}
            </>
          ) : (
            <p className="text-[12px] text-muted-foreground">—</p>
          )}
          <Row label="Background jobs" value={`${health.background.slotsInUse} / ${health.background.maxSlots}`} />
        </Card>
      </div>

      <ul className="space-y-1.5">
        {notes.map((n, i) => (
          <li key={i} className="flex items-start gap-2 text-[12.5px] leading-snug">
            <Info className={`mt-[2px] h-3.5 w-3.5 shrink-0 ${toneClass[n.tone]}`} />
            <span className="text-foreground/90">{n.text}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}
