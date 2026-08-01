"use client";

import type { ComponentProps } from "react";
import { GripVertical, Info, Loader2, Plus, RotateCcw } from "lucide-react";
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
  SortableContext,
  sortableKeyboardCoordinates,
  verticalListSortingStrategy,
  useSortable,
} from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { Button } from "@/components/ui/button";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import {
  type ExistingSource,
  type ProviderExtendedInfo,
  type SeriesExtendedInfo,
} from "@/lib/api/types";
import { AddSeries } from "@/components/comp/series/add-series";
import { ProviderCard } from "./provider-card";
import { useRegisterBottomBar } from "@/contexts/bottom-bar-context";

export interface ProviderSwitchState {
  useTitle: boolean;
  useCover: boolean;
  useStorage: boolean;
  useStatus: boolean;
}

export interface SourcesSectionProps {
  series: SeriesExtendedInfo;
  /** Already filtered to non-deleted providers. */
  providers: ProviderExtendedInfo[];
  /** Provider IDs in the current (possibly unsaved) priority order, highest first. */
  orderIds: string[];
  /** True when orderIds differs from the server's saved priorities. */
  orderDirty: boolean;
  /** Saving the current orderIds is in flight. */
  applyingOrder?: boolean;
  existingSources: ExistingSource[];
  providerSwitches: Record<string, ProviderSwitchState>;
  providerDisabledStates: Record<string, boolean>;
  providerFromChapters: Record<string, string>;
  providerDeletedStates: Record<string, boolean>;

  // Handlers
  onUseTitleChange: (providerId: string, value: boolean) => void;
  onUseCoverChange: (providerId: string, value: boolean) => void;
  onUseStorageChange: (providerId: string, value: boolean) => void;
  onUseStatusChange: (providerId: string, value: boolean) => void;
  onFromChapterChange: (providerId: string, value: string) => void;
  onEnableDisable: (providerId: string, disabled: boolean) => void;
  onDelete: (providerId: string) => void;
  /** Reorder this source's per-series priority (0 = highest) — local only, see onApplyOrder. */
  onMoveProvider: (providerId: string, direction: 'up' | 'down') => void;
  /** Drag-and-drop reorder — local only, given the full new provider-ID order. */
  onReorderProviders: (newOrder: string[]) => void;
  /** Persist the current orderIds — the only point this reorder UI talks to the API. */
  onApplyOrder: () => void | Promise<void>;
  /** Reset the local order to the configured global default (no-ops with a toast if unset). */
  onRevertToDefault: () => void;

  canEdit: boolean;
}

function SortableProviderCard({
  provider,
  canMoveUp,
  canMoveDown,
  ...cardProps
}: {
  provider: ProviderExtendedInfo;
  canMoveUp: boolean;
  canMoveDown: boolean;
} & Omit<ComponentProps<typeof ProviderCard>, "provider" | "canMoveUp" | "canMoveDown">) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({
    id: provider.id,
  });

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isDragging ? 0.5 : 1,
  };

  return (
    <div ref={setNodeRef} style={style} className="flex items-stretch gap-1">
      {cardProps.canEdit && (
        <button
          type="button"
          className="flex shrink-0 cursor-grab touch-none items-center px-1 text-muted-foreground/60 hover:text-foreground active:cursor-grabbing"
          aria-label={`Drag to reorder ${provider.provider}`}
          {...attributes}
          {...listeners}
        >
          <GripVertical className="h-4 w-4" />
        </button>
      )}
      <div className="min-w-0 flex-1">
        <ProviderCard provider={provider} canMoveUp={canMoveUp} canMoveDown={canMoveDown} {...cardProps} />
      </div>
    </div>
  );
}

export function SourcesSection({
  series,
  providers,
  orderIds,
  orderDirty,
  applyingOrder,
  existingSources,
  providerSwitches,
  providerDisabledStates,
  providerFromChapters,
  providerDeletedStates,
  onUseTitleChange,
  onUseCoverChange,
  onUseStorageChange,
  onUseStatusChange,
  onFromChapterChange,
  onEnableDisable,
  onDelete,
  onMoveProvider,
  onReorderProviders,
  onApplyOrder,
  onRevertToDefault,
  canEdit,
}: SourcesSectionProps) {
  const applyBarRef = useRegisterBottomBar(
    "sources-section-apply-bar",
    canEdit && providers.length > 1,
  );
  // Show sources in the (possibly unsaved) local priority order so the drag
  // handle / up-down controls line up with what's on screen.
  const orderIndex = new Map(orderIds.map((id, i) => [id, i]));
  const orderedProviders = [...providers].sort(
    (a, b) => (orderIndex.get(a.id) ?? 0) - (orderIndex.get(b.id) ?? 0),
  );

  const sensors = useSensors(
    useSensor(PointerSensor),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  const handleDragEnd = (event: DragEndEvent) => {
    const { active, over } = event;
    if (!over || active.id === over.id) return;
    const oldIndex = orderedProviders.findIndex(p => p.id === active.id);
    const newIndex = orderedProviders.findIndex(p => p.id === over.id);
    if (oldIndex < 0 || newIndex < 0) return;
    const next = [...orderedProviders.map(p => p.id)];
    const [moved] = next.splice(oldIndex, 1);
    next.splice(newIndex, 0, moved!);
    onReorderProviders(next);
  };

  const addSourceTrigger = (
    <Button size="sm" className="h-8 gap-1.5">
      <Plus className="h-3.5 w-3.5" />
      Add Source
    </Button>
  );

  const addSourceEmptyTrigger = (
    <Button variant="outline" size="sm" className="mt-3 gap-1.5">
      <Plus className="h-3.5 w-3.5" /> Add a source
    </Button>
  );

  return (
    <section className="space-y-4">
      <header className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-2">
          <h2 className="text-lg font-semibold tracking-tight">Sources</h2>
          <span className="inline-flex h-5 min-w-5 items-center justify-center rounded-full bg-foreground/10 px-1.5 text-[11px] font-medium tabular-nums text-muted-foreground">
            {providers.length}
          </span>
          <TooltipProvider delayDuration={200}>
            <Tooltip>
              <TooltipTrigger asChild>
                <button
                  type="button"
                  aria-label="How source order works"
                  className="inline-flex h-5 w-5 items-center justify-center rounded-full text-muted-foreground/70 transition-colors hover:text-foreground"
                >
                  <Info className="h-3.5 w-3.5" />
                </button>
              </TooltipTrigger>
              <TooltipContent side="right" className="max-w-[240px] text-left">
                Order sets source priority — the topmost source is preferred for
                reading, previews, and downloads. Drag, use the ▲▼ arrows, or
                focus a handle and press the arrow keys to reorder, then press
                Apply to save.
              </TooltipContent>
            </Tooltip>
          </TooltipProvider>
        </div>
        {canEdit && (
          <AddSeries
            title={series.title}
            existingSources={existingSources}
            seriesId={series.id}
            triggerButton={addSourceTrigger}
          />
        )}
      </header>

      <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={handleDragEnd}>
        <SortableContext items={orderedProviders.map(p => p.id)} strategy={verticalListSortingStrategy}>
          <div className="space-y-3">
            {orderedProviders.map((provider, index) => {
              const switches =
                providerSwitches[provider.id] ?? {
                  useTitle: false,
                  useCover: false,
                  useStorage: false,
                  useStatus: false,
                };
              const isDisabled = provider.isUninstalled
                ? true
                : providerDisabledStates[provider.id] ?? provider.isDisabled;
              const currentFromChapter =
                providerFromChapters[provider.id] ??
                provider.fromChapter?.toString() ??
                "";

              const updatedProvider: ProviderExtendedInfo = {
                ...provider,
                isDisabled,
              };

              return (
                <SortableProviderCard
                  key={provider.id}
                  provider={updatedProvider}
                  seriesId={series.id}
                  useCover={switches.useCover}
                  useTitle={switches.useTitle}
                  useStorage={switches.useStorage}
                  useStatus={switches.useStatus}
                  fromChapter={currentFromChapter}
                  onUseCoverChange={onUseCoverChange}
                  onUseTitleChange={onUseTitleChange}
                  onUseStorageChange={onUseStorageChange}
                  onUseStatusChange={onUseStatusChange}
                  onDisabledChange={onEnableDisable}
                  onDeleteProvider={onDelete}
                  onFromChapterChange={onFromChapterChange}
                  deletedProviderStates={providerDeletedStates}
                  onMove={onMoveProvider}
                  canMoveUp={index > 0}
                  canMoveDown={index < orderedProviders.length - 1}
                  canEdit={canEdit}
                />
              );
            })}
          </div>
        </SortableContext>
      </DndContext>

      {providers.length === 0 && (
        <div className="rounded-xl border border-dashed border-border/60 bg-card/50 p-8 text-center">
          <p className="text-sm text-muted-foreground">
            No sources configured yet.
          </p>
          {canEdit && (
            <AddSeries
              title={series.title}
              existingSources={existingSources}
              seriesId={series.id}
              triggerButton={addSourceEmptyTrigger}
            />
          )}
        </div>
      )}

      {/* Sticky apply bar — pinned to the bottom of the scrolling column (see
          the page's `lg:overflow-y-auto` left column) so it stays visible no
          matter how far the source list scrolls. Only shown once there are 2+
          sources (nothing to reorder with fewer) and canEdit. */}
      {canEdit && providers.length > 1 && (
        // z-40 (above the ActivityDock's z-30) — both are anchored near the
        // bottom of the viewport; without this the floating download dock
        // paints over this bar and swallows clicks on Apply/Revert.
        <div
          ref={applyBarRef}
          className="sticky bottom-0 z-40 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border/60 bg-background/95 px-3 py-3 backdrop-blur supports-[backdrop-filter]:bg-background/80"
        >
          <span className="text-xs text-muted-foreground">
            {orderDirty ? "Unsaved order — press Apply to save." : "Order matches saved priority."}
          </span>
          <div className="flex items-center gap-2">
            <Button type="button" variant="outline" size="sm" onClick={onRevertToDefault}>
              <RotateCcw className="mr-1.5 h-3.5 w-3.5" />
              Revert to Default
            </Button>
            <Button
              type="button"
              size="sm"
              onClick={() => void onApplyOrder()}
              disabled={!orderDirty || applyingOrder}
            >
              {applyingOrder ? <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" /> : null}
              Apply
            </Button>
          </div>
        </div>
      )}
    </section>
  );
}
