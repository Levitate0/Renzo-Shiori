"use client";

import * as React from "react";
import {
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  GripVertical,
  Loader2,
  RotateCcw,
  Sparkles,
} from "lucide-react";
import {
  DndContext,
  closestCenter,
  KeyboardSensor,
  PointerSensor,
  useSensor,
  useSensors,
  type DragEndEvent,
} from "@dnd-kit/core";
import {
  arrayMove,
  SortableContext,
  sortableKeyboardCoordinates,
  verticalListSortingStrategy,
  useSortable,
} from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { verticalSortableConstraints } from "@/lib/utils/dnd-constraints";
import { Button } from "@/components/ui/button";
import { useProviders } from "@/lib/api/hooks/useProviders";
import { useToast } from "@/hooks/use-toast";
import { useAuth } from "@/contexts/auth-context";
import { userService } from "@/lib/api/services/userService";
import { seriesService } from "@/lib/api/services/seriesService";
import { parsePriorityPrefs, mergePriorityPrefs } from "@/lib/utils/priority-prefs";
import { useRegisterBottomBar } from "@/contexts/bottom-bar-context";

/**
 * Builds the editable working list: the saved default order, filtered down to
 * providers that are still installed-for-me, followed by any installed
 * provider not yet in that order (alphabetical), so newly installed sources
 * always show up instead of silently being left out of the ranking.
 */
function buildWorkingOrder(saved: string[], installedNames: string[]): string[] {
  const installedSet = new Set(installedNames);
  const kept = saved.filter((name) => installedSet.has(name));
  const keptSet = new Set(kept);
  const rest = installedNames.filter((name) => !keptSet.has(name)).sort((a, b) => a.localeCompare(b));
  return [...kept, ...rest];
}

function arraysEqual(a: string[], b: string[]): boolean {
  return a.length === b.length && a.every((v, i) => v === b[i]);
}

function SortableProviderRow({
  name,
  index,
  total,
  onMove,
}: {
  name: string;
  index: number;
  total: number;
  onMove: (index: number, direction: "up" | "down") => void;
}) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({
    id: name,
  });

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isDragging ? 0.5 : 1,
    zIndex: isDragging ? 20 : undefined,
    position: isDragging ? ("relative" as const) : undefined,
  };

  return (
    <div
      ref={setNodeRef}
      style={style}
      className="flex items-center gap-2 rounded-lg border border-border/60 bg-card px-3 py-2"
    >
      <button
        type="button"
        className="flex shrink-0 cursor-grab touch-none items-center text-muted-foreground/70 hover:text-foreground active:cursor-grabbing"
        aria-label={`Drag to reorder ${name}`}
        {...attributes}
        {...listeners}
      >
        <GripVertical className="h-4 w-4" />
      </button>
      <span className="w-6 shrink-0 text-right text-xs tabular-nums text-muted-foreground/70">
        {index + 1}
      </span>
      <span className="min-w-0 flex-1 truncate text-sm font-medium">{name}</span>
      <div className="flex shrink-0 flex-col items-center justify-center gap-0.5">
        <button
          type="button"
          onClick={() => onMove(index, "up")}
          disabled={index === 0}
          title="Move up"
          aria-label={`Move ${name} up`}
          className="rounded p-0.5 text-muted-foreground transition-colors hover:bg-accent hover:text-foreground disabled:opacity-30 disabled:hover:bg-transparent"
        >
          <ChevronUp className="h-4 w-4" />
        </button>
        <button
          type="button"
          onClick={() => onMove(index, "down")}
          disabled={index === total - 1}
          title="Move down"
          aria-label={`Move ${name} down`}
          className="rounded p-0.5 text-muted-foreground transition-colors hover:bg-accent hover:text-foreground disabled:opacity-30 disabled:hover:bg-transparent"
        >
          <ChevronDown className="h-4 w-4" />
        </button>
      </div>
    </div>
  );
}

/**
 * Per-user "Default priority order" — a one-time-setup ranking of installed
 * sources by display name. Lives on the ACCOUNT (same preferences blob as
 * theme), not instance-wide settings — every user ranks their own sources.
 *
 * Reordering here is purely local until Apply is pressed (same "buffer, then
 * commit" model as the per-series order), so dragging through a full reorder
 * doesn't fire a save per step. Apply only saves the ranking; it does NOT
 * touch any series. "Apply to All" is the separate, bigger action that pushes
 * this ranking onto every series you own AND turns on the redownload-on-
 * upgrade system for you — see its own confirmation dialog below.
 */
export function DefaultPriorityOrderTab() {
  const { user, refreshAuth } = useAuth();
  const { data: providers } = useProviders();
  const { toast } = useToast();
  const [saving, setSaving] = React.useState(false);
  const [applyingToAll, setApplyingToAll] = React.useState(false);
  const applyBarRef = useRegisterBottomBar("default-priority-order-apply-bar", true);

  const installedNames = React.useMemo(
    () =>
      Array.from(
        new Set((providers ?? []).filter((p) => p.isEnabledForMe).map((p) => p.name)),
      ),
    [providers],
  );

  const prefs = React.useMemo(() => parsePriorityPrefs(user?.preferences), [user?.preferences]);
  const savedOrder = prefs.defaultSourcePriorityOrder ?? [];
  const isConfigured = savedOrder.length > 0;
  const redownloadEnabled = prefs.redownloadFromHigherPrioritySources ?? false;

  const [order, setOrder] = React.useState<string[]>([]);
  const [initialized, setInitialized] = React.useState(false);

  // Seed (and re-seed if the installed source set changes) without clobbering
  // in-progress reordering the user hasn't applied yet.
  React.useEffect(() => {
    if (!providers) return;
    setOrder((prev) => {
      const next = buildWorkingOrder(isConfigured ? savedOrder : [...installedNames].sort((a, b) => a.localeCompare(b)), installedNames);
      // Once initialized, preserve the user's in-progress arrangement for
      // names both lists already agree on; only append truly-new sources.
      if (!initialized) return next;
      const prevSet = new Set(prev);
      const additions = next.filter((n) => !prevSet.has(n));
      const stillInstalled = prev.filter((n) => installedNames.includes(n));
      return additions.length > 0 ? [...stillInstalled, ...additions] : stillInstalled;
    });
    setInitialized(true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [installedNames, savedOrder.join("|"), isConfigured]);

  const dirty = isConfigured
    ? !arraysEqual(order, savedOrder.filter((n) => installedNames.includes(n)).concat(
        order.filter((n) => !savedOrder.includes(n)),
      ))
    : order.length > 0; // unconfigured → any arrangement is a pending "set up"

  const sensors = useSensors(
    useSensor(PointerSensor),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  const handleDragEnd = (event: DragEndEvent) => {
    const { active, over } = event;
    if (!over || active.id === over.id) return;
    setOrder((prev) => {
      const oldIndex = prev.indexOf(active.id as string);
      const newIndex = prev.indexOf(over.id as string);
      if (oldIndex < 0 || newIndex < 0) return prev;
      return arrayMove(prev, oldIndex, newIndex);
    });
  };

  const handleMove = (index: number, direction: "up" | "down") => {
    setOrder((prev) => {
      const swap = direction === "up" ? index - 1 : index + 1;
      if (swap < 0 || swap >= prev.length) return prev;
      const next = [...prev];
      [next[index], next[swap]] = [next[swap]!, next[index]!];
      return next;
    });
  };

  const handleApply = async () => {
    setSaving(true);
    try {
      await userService.updateMe({
        preferences: mergePriorityPrefs(user?.preferences, { defaultSourcePriorityOrder: order }),
      });
      await refreshAuth();
      toast({ title: "Default priority order saved" });
    } catch {
      toast({ variant: "destructive", title: "Failed to save default priority order" });
    } finally {
      setSaving(false);
    }
  };

  const handleReset = () => {
    setOrder(isConfigured ? buildWorkingOrder(savedOrder, installedNames) : []);
  };

  // Applies the SAVED order (not the unsaved local buffer — press Apply
  // first if you've been rearranging) to every series you own, and turns on
  // the redownload-on-upgrade system for you. This is the "go live" action.
  const handleApplyToAll = async () => {
    if (dirty) {
      toast({
        title: "Save your order first",
        description: "Press Apply above before applying it to your library.",
      });
      return;
    }
    setApplyingToAll(true);
    try {
      const result = await seriesService.applyDefaultPriorityToAll();
      if (!result.success) {
        toast({ title: "Nothing to apply", description: result.error ?? "Set up an order above first." });
        return;
      }
      await refreshAuth();
      toast({
        title: "Applied to your library",
        description: `${result.seriesReordered} of ${result.seriesConsidered} series reordered` +
          (result.seriesAdopted > 0 ? `, ${result.seriesAdopted} series adopted` : "") +
          (result.chaptersQueued > 0 ? `, ${result.chaptersQueued} chapter re-download(s) queued` : "") +
          ". Redownload-on-upgrade is now on for you.",
      });
    } catch (err) {
      toast({
        variant: "destructive",
        title: "Failed to apply to all series",
        description: err instanceof Error ? err.message : undefined,
      });
    } finally {
      setApplyingToAll(false);
    }
  };

  return (
    <div className="space-y-4">
      {/* Accent-colored, hard-to-miss callout — this is a one-time setup step
          most installs skip, and skipping it silently means nothing below
          ever activates. */}
      <div
        className={
          isConfigured
            ? "flex items-start gap-3 rounded-xl border border-primary/30 bg-primary/[0.06] p-4"
            : "flex items-start gap-3 rounded-xl border-2 border-primary bg-primary/15 p-4 shadow-[0_0_0_1px_hsl(var(--primary)/0.25),0_0_24px_hsl(var(--primary)/0.25)]"
        }
      >
        <Sparkles className={isConfigured ? "mt-0.5 h-5 w-5 shrink-0 text-primary/80" : "mt-0.5 h-5 w-5 shrink-0 text-primary"} />
        <div className="min-w-0 space-y-1">
          <p className={isConfigured ? "text-sm font-semibold text-primary/90" : "text-sm font-bold uppercase tracking-wide text-primary"}>
            {isConfigured ? "Default priority order is set up" : "Highly recommended to be set up"}
          </p>
          <p className="text-sm text-muted-foreground">
            Rank your sources once, here — it&apos;s just for you, not shared with
            other accounts — and every new series you add starts with this
            priority automatically. It&apos;s also available as &ldquo;Revert to
            Default&rdquo; on any existing series. Pressing <strong>Apply</strong>{" "}
            below only saves this ranking; nothing changes on any series until
            you also use <strong>Apply to All</strong>.
          </p>
          {redownloadEnabled && (
            <p className="flex items-center gap-1.5 pt-1 text-xs font-medium text-primary/90">
              <CheckCircle2 className="h-3.5 w-3.5" />
              Redownload-on-upgrade is currently ON for your series.
            </p>
          )}
        </div>
      </div>

      {order.length === 0 ? (
        <div className="rounded-xl border border-dashed border-border/60 bg-card/50 p-8 text-center text-sm text-muted-foreground">
          No installed sources to rank yet — install some sources first.
        </div>
      ) : (
        <DndContext
          sensors={sensors}
          collisionDetection={closestCenter}
          onDragEnd={handleDragEnd}
          modifiers={verticalSortableConstraints}
        >
          <SortableContext items={order} strategy={verticalListSortingStrategy}>
            <div className="space-y-2 overflow-x-clip pb-20">
              {order.map((name, index) => (
                <SortableProviderRow
                  key={name}
                  name={name}
                  index={index}
                  total={order.length}
                  onMove={handleMove}
                />
              ))}
            </div>
          </SortableContext>
        </DndContext>
      )}

      {/* Sticky apply bar — stays visible while the list above scrolls, so a
          long source list never hides the way to actually commit a reorder.
          z-40 (above the ActivityDock's z-30): both are anchored near the
          bottom of the viewport, and without this the floating download dock
          would paint over the bar and swallow clicks on Apply/Revert. */}
      <div
        ref={applyBarRef}
        className="sticky bottom-0 z-40 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border/60 bg-background/95 px-4 py-3 backdrop-blur supports-[backdrop-filter]:bg-background/80"
      >
        <span className="text-xs text-muted-foreground">
          {dirty ? "Unsaved changes — press Apply to save." : "No unsaved changes."}
        </span>
        <div className="flex items-center gap-2">
          <Button type="button" variant="outline" size="sm" onClick={handleReset} disabled={!dirty}>
            <RotateCcw className="mr-1.5 h-3.5 w-3.5" />
            Reset
          </Button>
          <Button type="button" variant="outline" size="sm" onClick={handleApply} disabled={!dirty || saving}>
            {saving ? <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" /> : null}
            Apply
          </Button>
          <Button
            type="button"
            size="sm"
            onClick={() => void handleApplyToAll()}
            disabled={!isConfigured || applyingToAll}
            title="Reorder every series you own to match this order, and turn on redownload-on-upgrade"
          >
            {applyingToAll ? <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" /> : null}
            Apply to All Series
          </Button>
        </div>
      </div>
    </div>
  );
}
