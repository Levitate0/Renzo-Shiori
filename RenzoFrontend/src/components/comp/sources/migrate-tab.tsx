"use client";

import * as React from "react";
import Image from "next/image";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  Loader2,
  MinusCircle,
  XCircle,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Progress } from "@/components/ui/progress";
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { ApiError } from "@/lib/api/client";
import { migrateService } from "@/lib/api/services/migrateService";
import {
  type MigrationCandidate,
  type MigrationFrom,
  type MigrationItem,
  type MigrationRun,
  type MigrationTarget,
  type SourceSpeed,
} from "@/lib/api/types";
import { formatThumbnailUrl } from "@/lib/utils/thumbnail";

/*
 * Sources › Migrate: move every series on a source to other sources, for when a
 * site breaks or blocks you (server: Services/Series/SourceMigrationService.cs).
 *
 * Hub twin: hub/feature-shiori/src/jvmShared/kotlin/app/renzoshiori/client/ui/sources/MigrateTab.kt
 * — same views, same wording; change one, change both.
 *
 * One run per user lives on the server, so the tab picks it up again after
 * navigating away: start → progress (searching) → preview (ready) → progress
 * (applying) → results (done); "failed" shows the error.
 */

const SOURCES_KEY = ["migrate", "sources"] as const;
const POLL_MS = 2000;

/**
 * The preview's choices per run, kept while the page is open: leaving the tab
 * unmounts it, and coming back must not re-tick what was unticked or undo a
 * picked match. Hub twin: MigrateTab.kt savedMigrateChoices.
 */
type Choices = { checked: Record<string, boolean>; picks: Record<string, string>; oldSource: "disable" | "keep" };
const savedChoices = new Map<string, Choices>();

/**
 * "{provider}", plus " ({language})" unless the language is "en" or "". Used for
 * the from rows and the target rows alike. Hub twin: MigrateTab.kt migrateSourceName.
 */
function sourceName(provider: string, language: string): string {
  return language && language !== "en" ? `${provider} (${language})` : provider;
}

function fromKey(f: MigrationFrom): string {
  return `${f.provider}\n${f.language}`;
}

/** Hub twin: MigrateTab.kt migrateSpeedLine. */
function speedLine(s: SourceSpeed): string {
  if (s.attempts === 0) return "No downloads lately";
  const rate = `${Math.round((s.successRate ?? 0) * 100)}% downloaded`;
  return s.medianSeconds == null ? rate : `≈ ${Math.round(s.medianSeconds)} s a chapter · ${rate}`;
}

function percent(coverage: number): number {
  return Math.round(coverage * 100);
}

/** A candidate in a match menu. Hub twin: MigrateTab.kt migrateCandidateLabel. */
function candidateLabel(c: MigrationCandidate): string {
  return `${c.provider}: ${c.title} · ${percent(c.coverage)}%${c.suspect ? " · many more chapters" : ""}`;
}

/** Under a chosen candidate the server flagged `suspect`. Hub twin: MigrateTab.kt SUSPECT_NOTE. */
const SUSPECT_NOTE = "Lists many more chapters than this series: it may be another series with the same name.";

function byTitle(a: MigrationItem, b: MigrationItem): number {
  return a.title.localeCompare(b.title);
}

/** The candidate a row will migrate to: the one picked under "Other matches", else the match. */
function chosenCandidate(item: MigrationItem, picks: Record<string, string>): MigrationCandidate | null {
  const picked = picks[item.seriesId];
  if (picked) {
    const found = [item.match, ...item.alternatives].find((c) => c?.mihonId === picked);
    if (found) return found;
  }
  return item.match;
}

function errorText(err: unknown, fallback: string): string {
  return err instanceof Error && err.message ? err.message : fallback;
}

// ---------------------------------------------------------------------------
// Tab
// ---------------------------------------------------------------------------

export function MigrateTab() {
  const queryClient = useQueryClient();
  // undefined = still asking the server for the current run; null = none.
  const [run, setRun] = React.useState<MigrationRun | null | undefined>(undefined);
  // seriesId → the candidate's mihonId picked in the preview, kept so the
  // results can say which source was added.
  const [picks, setPicks] = React.useState<Record<string, string>>({});
  const [pollRetry, setPollRetry] = React.useState(0);

  React.useEffect(() => {
    let cancelled = false;
    migrateService
      .current()
      .then((r) => {
        if (cancelled) return;
        setPicks((r && savedChoices.get(r.id)?.picks) ?? {});
        setRun(r);
      })
      .catch(() => {
        if (!cancelled) setRun(null);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  // Searching / applying: poll every 2 s until the phase moves on.
  const busy = run?.phase === "searching" || run?.phase === "applying";
  React.useEffect(() => {
    if (!run || !busy) return;
    let cancelled = false;
    const id = run.id;
    const timer = window.setTimeout(() => {
      migrateService
        .get(id)
        .then((next) => {
          if (!cancelled) setRun(next);
        })
        .catch((err: unknown) => {
          if (cancelled) return;
          if (err instanceof ApiError && err.status === 404) setRun(null);
          else setPollRetry((n) => n + 1); // a blip: try again in 2 s
        });
    }, POLL_MS);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [run, busy, pollRetry]);

  /** "Start over" / "Done": forget the run on the server, back to the start view. */
  const backToStart = React.useCallback(async () => {
    if (run) {
      savedChoices.delete(run.id);
      try {
        await migrateService.discard(run.id);
      } catch {
        // Already gone, or still busy: the start view says so if it matters.
      }
    }
    setPicks({});
    setRun(null);
    void queryClient.invalidateQueries({ queryKey: SOURCES_KEY });
  }, [run, queryClient]);

  if (run === undefined) {
    return (
      <div className="flex items-center justify-center p-8 text-muted-foreground">
        <Loader2 className="h-5 w-5 animate-spin" />
      </div>
    );
  }

  if (run === null) return <StartView onStarted={setRun} />;

  switch (run.phase) {
    case "searching":
    case "applying":
      return (
        <ProgressView
          key={run.id + run.phase}
          run={run}
          onCancel={run.phase === "searching" ? () => void backToStart() : undefined}
        />
      );
    case "ready":
      return (
        <PreviewView
          key={run.id}
          run={run}
          picks={picks}
          setPicks={setPicks}
          onApplied={setRun}
          onStartOver={() => void backToStart()}
        />
      );
    case "done":
      return <ResultsView run={run} picks={picks} onDone={() => void backToStart()} />;
    default:
      return (
        <div className="space-y-3 rounded-xl border border-destructive/40 bg-destructive/10 p-4">
          <p className="text-sm text-destructive">{run.error ?? "The migration failed."}</p>
          <Button type="button" variant="outline" size="sm" onClick={() => void backToStart()}>
            Start over
          </Button>
        </div>
      );
  }
}

// ---------------------------------------------------------------------------
// Start view
// ---------------------------------------------------------------------------

function StartView({ onStarted }: { onStarted: (run: MigrationRun) => void }) {
  const { data, isLoading, error } = useQuery({
    queryKey: SOURCES_KEY,
    queryFn: () => migrateService.sources(),
  });

  const [selectedFrom, setSelectedFrom] = React.useState<string | null>(null);
  // Every target id in on-screen order, and which are ticked.
  const [order, setOrder] = React.useState<string[]>([]);
  const [checked, setChecked] = React.useState<Set<string>>(new Set());
  const seen = React.useRef(new Set<string>());
  const [starting, setStarting] = React.useState(false);
  const [startError, setStartError] = React.useState<string | null>(null);

  // Seed from the server's order (suggested first), ticking the suggested ones;
  // a refetch keeps the user's arrangement and only appends new sources.
  React.useEffect(() => {
    if (!data) return;
    const ids = new Set(data.targets.map((t) => t.id));
    const fresh = data.targets.filter((t) => !seen.current.has(t.id));
    fresh.forEach((t) => seen.current.add(t.id));
    setOrder((prev) => [...prev.filter((id) => ids.has(id)), ...fresh.map((t) => t.id)]);
    setChecked((prev) => {
      const next = new Set([...prev].filter((id) => ids.has(id)));
      fresh.filter((t) => t.suggested).forEach((t) => next.add(t.id));
      return next;
    });
  }, [data]);

  const from = data?.from.find((f) => fromKey(f) === selectedFrom) ?? null;

  const targetsById = React.useMemo(
    () => new Map((data?.targets ?? []).map((t) => [t.id, t] as const)),
    [data],
  );
  const visibleTargets = order
    .map((id) => targetsById.get(id))
    .filter((t): t is MigrationTarget => !!t && t.provider !== from?.provider);
  const chosenTargets = visibleTargets.filter((t) => checked.has(t.id)).map((t) => t.id);

  const move = (index: number, direction: "up" | "down") => {
    const other = direction === "up" ? index - 1 : index + 1;
    const a = visibleTargets[index]?.id;
    const b = visibleTargets[other]?.id;
    if (!a || !b) return;
    setOrder((prev) => {
      const next = [...prev];
      const ia = next.indexOf(a);
      const ib = next.indexOf(b);
      if (ia < 0 || ib < 0) return prev;
      next[ia] = b;
      next[ib] = a;
      return next;
    });
  };

  const toggle = (id: string, on: boolean) =>
    setChecked((prev) => {
      const next = new Set(prev);
      if (on) next.add(id);
      else next.delete(id);
      return next;
    });

  const findMatches = async () => {
    if (!from || chosenTargets.length === 0) return;
    setStarting(true);
    setStartError(null);
    try {
      const started = await migrateService.start({
        from: from.provider,
        language: from.language,
        targets: chosenTargets,
      });
      setStarting(false);
      onStarted(started);
    } catch (err) {
      setStartError(errorText(err, "Could not start the migration."));
      setStarting(false);
    }
  };

  if (isLoading) {
    return (
      <div className="flex items-center justify-center p-8 text-muted-foreground">
        <Loader2 className="h-5 w-5 animate-spin" />
      </div>
    );
  }
  if (error || !data) {
    return <p className="p-4 text-sm text-destructive">{errorText(error, "Could not load the sources.")}</p>;
  }

  return (
    <div className="space-y-6">
      <section className="space-y-3">
        <div className="space-y-1">
          <h3 className="text-sm font-semibold">Migrate from</h3>
          <p className="text-sm text-muted-foreground">
            Move every series on a source to other sources, for when a site breaks or blocks you.
            Downloads and read progress stay; the new source is added to each series.
          </p>
        </div>
        <RadioGroup
          value={selectedFrom ?? ""}
          onValueChange={(v) => setSelectedFrom(v)}
          className="gap-2"
        >
          {data.from.map((f, i) => {
            const key = fromKey(f);
            const id = `migrate-from-${i}`;
            return (
              <label
                key={key}
                htmlFor={id}
                className={
                  "flex cursor-pointer items-center gap-3 rounded-lg border px-3 py-2 transition-colors " +
                  (selectedFrom === key
                    ? "border-primary/60 bg-primary/[0.06]"
                    : "border-border/60 bg-card hover:bg-accent/40")
                }
              >
                <RadioGroupItem value={key} id={id} className="shrink-0" />
                <div className="min-w-0 flex-1">
                  <div className="truncate text-sm font-medium">{sourceName(f.provider, f.language)}</div>
                  <div className="text-xs text-muted-foreground">
                    {f.seriesCount} series
                    {f.disabledCount > 0 ? ` · ${f.disabledCount} switched off` : ""}
                  </div>
                  <div className="text-xs text-muted-foreground">{speedLine(f.speed)}</div>
                </div>
              </label>
            );
          })}
        </RadioGroup>
      </section>

      <section className="space-y-3">
        <div className="space-y-1">
          <h3 className="text-sm font-semibold">Look for them on</h3>
          <p className="text-sm text-muted-foreground">
            Tried in this order; the first that lists nine in ten of a series&apos; chapters is used.
          </p>
        </div>
        <div className="space-y-2">
          {visibleTargets.map((t, index) => {
            const id = `migrate-target-${t.id}`;
            return (
              <div
                key={t.id}
                className="flex items-center gap-3 rounded-lg border border-border/60 bg-card px-3 py-2"
              >
                <Checkbox id={id} checked={checked.has(t.id)} onCheckedChange={(on) => toggle(t.id, on)} />
                <label htmlFor={id} className="min-w-0 flex-1 cursor-pointer">
                  <div className="truncate text-sm font-medium">{sourceName(t.provider, t.language)}</div>
                  <div className="text-xs text-muted-foreground">{speedLine(t.speed)}</div>
                </label>
                {/* Same up/down control as the Default priority order tab. */}
                <div className="flex shrink-0 flex-col items-center justify-center gap-0.5">
                  <button
                    type="button"
                    onClick={() => move(index, "up")}
                    disabled={index === 0}
                    title="Move up"
                    aria-label={`Move ${t.provider} up`}
                    className="rounded p-0.5 text-muted-foreground transition-colors hover:bg-accent hover:text-foreground disabled:opacity-30 disabled:hover:bg-transparent"
                  >
                    <ChevronUp className="h-4 w-4" />
                  </button>
                  <button
                    type="button"
                    onClick={() => move(index, "down")}
                    disabled={index === visibleTargets.length - 1}
                    title="Move down"
                    aria-label={`Move ${t.provider} down`}
                    className="rounded p-0.5 text-muted-foreground transition-colors hover:bg-accent hover:text-foreground disabled:opacity-30 disabled:hover:bg-transparent"
                  >
                    <ChevronDown className="h-4 w-4" />
                  </button>
                </div>
              </div>
            );
          })}
        </div>
      </section>

      <div className="space-y-2">
        <Button
          type="button"
          onClick={() => void findMatches()}
          disabled={!from || chosenTargets.length === 0 || starting}
        >
          {starting ? <Loader2 className="mr-1.5 h-4 w-4 animate-spin" /> : null}
          Find matches
        </Button>
        {startError && <p className="text-sm text-destructive">{startError}</p>}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Progress view (searching / applying)
// ---------------------------------------------------------------------------

/** `onCancel` only while searching: the server stops the search; an apply runs to the end. */
function ProgressView({ run, onCancel }: { run: MigrationRun; onCancel?: () => void }) {
  const [cancelling, setCancelling] = React.useState(false);
  const verb = run.phase === "applying" ? "Migrating" : "Searching";
  const value = run.total > 0 ? (run.done / run.total) * 100 : 0;
  return (
    <div className="space-y-3 rounded-xl border border-border/60 bg-card p-4">
      <p className="flex items-center gap-2 text-sm">
        <Loader2 className="h-4 w-4 shrink-0 animate-spin text-muted-foreground" />
        {verb} {run.done} of {run.total} series…
      </p>
      <Progress value={value} className="h-2" />
      {onCancel && (
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={cancelling}
          onClick={() => {
            setCancelling(true);
            onCancel();
          }}
        >
          Cancel
        </Button>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Preview view (ready)
// ---------------------------------------------------------------------------

function SeriesCover({ item }: { item: MigrationItem }) {
  return (
    <div className="relative h-12 w-8 shrink-0 overflow-hidden rounded">
      <Image
        src={formatThumbnailUrl(item.thumbnailUrl ?? undefined)}
        alt={item.title}
        fill
        sizes="32px"
        className="object-cover"
      />
    </div>
  );
}

function PreviewView({
  run,
  picks,
  setPicks,
  onApplied,
  onStartOver,
}: {
  run: MigrationRun;
  picks: Record<string, string>;
  setPicks: React.Dispatch<React.SetStateAction<Record<string, string>>>;
  onApplied: (run: MigrationRun) => void;
  onStartOver: () => void;
}) {
  const matched = React.useMemo(
    () => run.items.filter((i) => i.status === "matched" && i.match).sort(byTitle),
    [run.items],
  );
  // Listed with a checkbox: the matched, and any other row once one of its
  // candidates is picked ("Found only titles with many more chapters… check
  // before choosing"). Hub twin: MigrateTab.kt PreviewView `listed`.
  const listed = React.useMemo(
    () => run.items.filter((i) => chosenCandidate(i, picks) != null).sort(byTitle),
    [run.items, picks],
  );
  const rest = React.useMemo(
    () => run.items.filter((i) => chosenCandidate(i, picks) == null).sort(byTitle),
    [run.items, picks],
  );

  // Ticked when the match lists at least 80% of the old source's chapters,
  // unless this run's choices were made already.
  const [checked, setChecked] = React.useState<Record<string, boolean>>(
    () =>
      savedChoices.get(run.id)?.checked ??
      Object.fromEntries(matched.map((i) => [i.seriesId, (i.match?.coverage ?? 0) >= 0.8])),
  );
  const [oldSource, setOldSource] = React.useState<"disable" | "keep">(
    () => savedChoices.get(run.id)?.oldSource ?? "disable",
  );
  React.useEffect(() => {
    savedChoices.set(run.id, { checked, picks, oldSource });
  }, [run.id, checked, picks, oldSource]);
  const [applying, setApplying] = React.useState(false);
  const [applyError, setApplyError] = React.useState<string | null>(null);

  const chosenRows = listed.filter((i) => checked[i.seriesId]);

  const migrate = async () => {
    const items = chosenRows
      .map((i) => ({ seriesId: i.seriesId, mihonId: chosenCandidate(i, picks)?.mihonId ?? "" }))
      .filter((i) => i.mihonId);
    if (items.length === 0) return;
    setApplying(true);
    setApplyError(null);
    try {
      const next = await migrateService.apply(run.id, { items, oldSource });
      setApplying(false);
      onApplied(next);
    } catch (err) {
      setApplyError(errorText(err, "Could not start migrating."));
      setApplying(false);
    }
  };

  return (
    <div className="space-y-4">
      <p className="text-sm font-medium">
        {matched.length} of {run.total} series found on the chosen sources
      </p>

      <div className="space-y-2 pb-4">
        {listed.map((item) => {
          const c = chosenCandidate(item, picks);
          if (!c) return null;
          // "Other matches": every candidate but the one shown, so a pick can be
          // undone (before any pick that is exactly the alternatives).
          // Hub twin: MigrateTab.kt MatchedRow → OtherMatchesPicker.
          const others = [item.match, ...item.alternatives].filter(
            (o): o is MigrationCandidate => !!o && o.mihonId !== c.mihonId,
          );
          const id = `migrate-item-${item.seriesId}`;
          return (
            <div
              key={item.seriesId}
              className="flex items-start gap-3 rounded-lg border border-border/60 bg-card px-3 py-2"
            >
              <Checkbox
                id={id}
                className="mt-4"
                checked={!!checked[item.seriesId]}
                onCheckedChange={(on) => setChecked((prev) => ({ ...prev, [item.seriesId]: on }))}
              />
              <SeriesCover item={item} />
              <div className="min-w-0 flex-1 space-y-0.5">
                <label htmlFor={id} className="block cursor-pointer truncate text-sm font-medium">
                  {item.title}
                </label>
                <div className="break-words text-xs text-muted-foreground">
                  → {c.provider}: {c.title}
                  {c.foundAs ? ` · found as “${c.foundAs}”` : ""}
                </div>
                <div className="text-xs text-muted-foreground">
                  {c.chapterCount} chapters
                  {c.latest != null ? ` · up to ${c.latest}` : ""}
                  {` · ${percent(c.coverage)}% of ${item.chapters}`}
                </div>
                {c.suspect && <div className="text-xs text-amber-500 dark:text-amber-400">{SUSPECT_NOTE}</div>}
                {others.length > 0 && (
                  <DropdownMenu>
                    <DropdownMenuTrigger asChild>
                      <button
                        type="button"
                        className="inline-flex items-center gap-1 rounded px-1 py-0.5 text-xs text-primary hover:bg-accent"
                      >
                        Other matches
                        <ChevronDown className="h-3 w-3" />
                      </button>
                    </DropdownMenuTrigger>
                    <DropdownMenuContent align="start" className="max-w-[min(28rem,90vw)]">
                      {others.map((o) => (
                        <DropdownMenuItem
                          key={o.mihonId}
                          onSelect={() => setPicks((prev) => ({ ...prev, [item.seriesId]: o.mihonId }))}
                        >
                          <span className="truncate">{candidateLabel(o)}</span>
                        </DropdownMenuItem>
                      ))}
                    </DropdownMenuContent>
                  </DropdownMenu>
                )}
              </div>
            </div>
          );
        })}

        {rest.map((item) => (
          <div key={item.seriesId} className="rounded-lg border border-border/40 px-3 py-2">
            <div className="truncate text-sm font-medium">{item.title}</div>
            {item.message && <div className="text-xs text-muted-foreground">{item.message}</div>}
            {item.alternatives.length > 0 && (
              // Picking one lists the row above, ticked. Hub twin: OtherMatchesPicker "Choose a match".
              <DropdownMenu>
                <DropdownMenuTrigger asChild>
                  <button
                    type="button"
                    className="inline-flex items-center gap-1 rounded px-1 py-0.5 text-xs text-primary hover:bg-accent"
                  >
                    Choose a match
                    <ChevronDown className="h-3 w-3" />
                  </button>
                </DropdownMenuTrigger>
                <DropdownMenuContent align="start" className="max-w-[min(28rem,90vw)]">
                  {item.alternatives.map((o) => (
                    <DropdownMenuItem
                      key={o.mihonId}
                      onSelect={() => {
                        setPicks((prev) => ({ ...prev, [item.seriesId]: o.mihonId }));
                        setChecked((prev) => ({ ...prev, [item.seriesId]: true }));
                      }}
                    >
                      <span className="truncate">{candidateLabel(o)}</span>
                    </DropdownMenuItem>
                  ))}
                </DropdownMenuContent>
              </DropdownMenu>
            )}
          </div>
        ))}
      </div>

      {/* Sticky action bar, as on the Default priority order tab: a long list
          never hides the way to commit. */}
      <div className="sticky bottom-0 z-40 space-y-2 rounded-lg border border-border/60 bg-background/95 px-4 py-3 backdrop-blur supports-[backdrop-filter]:bg-background/80">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex flex-wrap items-center gap-3 text-sm">
            <span className="text-muted-foreground">Old source:</span>
            <RadioGroup
              value={oldSource}
              onValueChange={(v) => setOldSource(v === "keep" ? "keep" : "disable")}
              className="flex flex-wrap items-center gap-4"
            >
              <label htmlFor="migrate-old-disable" className="flex cursor-pointer items-center gap-2">
                <RadioGroupItem value="disable" id="migrate-old-disable" />
                Switch it off
              </label>
              <label htmlFor="migrate-old-keep" className="flex cursor-pointer items-center gap-2">
                <RadioGroupItem value="keep" id="migrate-old-keep" />
                Leave it on
              </label>
            </RadioGroup>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Button type="button" variant="outline" size="sm" onClick={onStartOver} disabled={applying}>
              Start over
            </Button>
            <Button
              type="button"
              size="sm"
              onClick={() => void migrate()}
              disabled={chosenRows.length === 0 || applying}
            >
              {applying ? <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" /> : null}
              Migrate {chosenRows.length} series
            </Button>
          </div>
        </div>
        {applyError && <p className="text-sm text-destructive">{applyError}</p>}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Results view (done)
// ---------------------------------------------------------------------------

function ResultsView({
  run,
  picks,
  onDone,
}: {
  run: MigrationRun;
  picks: Record<string, string>;
  onDone: () => void;
}) {
  const count = (status: MigrationItem["status"]) => run.items.filter((i) => i.status === status).length;
  // Only what the apply touched (plus searches that failed), by title; rows left
  // unticked or not found are not listed. Hub twin: MigrateTab.kt ResultsView.
  const finished = run.items
    .filter((i) => i.status === "migrated" || i.status === "skipped" || i.status === "failed")
    .sort(byTitle);

  return (
    <div className="space-y-4">
      <p className="text-sm font-medium">
        Migrated {count("migrated")} · Skipped {count("skipped")} · Failed {count("failed")}
      </p>
      <div className="space-y-2">
        {finished.map((item) => (
          <div
            key={item.seriesId}
            className="flex items-center gap-3 rounded-lg border border-border/60 bg-card px-3 py-2"
          >
            {item.status === "migrated" ? (
              <CheckCircle2 className="h-4 w-4 shrink-0 text-primary" />
            ) : item.status === "skipped" ? (
              <MinusCircle className="h-4 w-4 shrink-0 text-muted-foreground" />
            ) : (
              <XCircle className="h-4 w-4 shrink-0 text-destructive" />
            )}
            <div className="min-w-0 flex-1">
              <div className="truncate text-sm font-medium">{item.title}</div>
              <div className="text-xs text-muted-foreground">
                {item.status === "migrated"
                  ? `Added ${chosenCandidate(item, picks)?.provider ?? ""}`
                  : item.message}
              </div>
            </div>
          </div>
        ))}
      </div>
      <Button type="button" onClick={onDone}>
        Done
      </Button>
    </div>
  );
}
