"use client";

import React from "react";
import { GripVertical } from "lucide-react";
import {
  DndContext,
  closestCenter,
  KeyboardSensor,
  PointerSensor,
  useSensor,
  useSensors,
  type DragEndEvent,
  type DraggableAttributes,
  type DraggableSyntheticListeners,
} from "@dnd-kit/core";
import {
  arrayMove,
  SortableContext,
  sortableKeyboardCoordinates,
  useSortable,
  verticalListSortingStrategy,
} from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";

import { verticalSortableConstraints } from "@/lib/utils/dnd-constraints";

/**
 * A vertical drag-to-reorder list for short lists of names: the categories in
 * Settings and the series page's Category picker, and the collections dialog.
 * Rows move only by their grip (so a tap on the row still picks it), and a
 * focused grip moves with the arrow keys (dnd-kit's KeyboardSensor).
 *
 * `onReorder` gets the whole new order once, on drop, and only when it changed.
 *
 * Hub twin: hub/feature-shiori/.../ui/components/DragReorder.kt.
 */
export function SortableRows({
  ids,
  onReorder,
  className,
  children,
}: {
  ids: string[];
  onReorder: (ids: string[]) => void;
  className?: string;
  children: React.ReactNode;
}) {
  const sensors = useSensors(
    useSensor(PointerSensor),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  const handleDragEnd = (event: DragEndEvent) => {
    const { active, over } = event;
    if (!over || active.id === over.id) return;
    const from = ids.indexOf(String(active.id));
    const to = ids.indexOf(String(over.id));
    if (from < 0 || to < 0) return;
    onReorder(arrayMove(ids, from, to));
  };

  return (
    <DndContext
      sensors={sensors}
      collisionDetection={closestCenter}
      modifiers={verticalSortableConstraints}
      onDragEnd={handleDragEnd}
    >
      <SortableContext items={ids} strategy={verticalListSortingStrategy}>
        <div className={className}>{children}</div>
      </SortableContext>
    </DndContext>
  );
}

export interface DragHandleProps {
  attributes: DraggableAttributes;
  listeners: DraggableSyntheticListeners;
}

/** One row of a {@link SortableRows}. `children` places the grip with {@link DragHandle}. */
export function SortableRow({
  id,
  className,
  children,
}: {
  id: string;
  className?: string;
  children: (handle: DragHandleProps) => React.ReactNode;
}) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id });
  return (
    <div
      ref={setNodeRef}
      style={{
        transform: CSS.Transform.toString(transform),
        transition,
        opacity: isDragging ? 0.5 : 1,
        position: "relative",
        zIndex: isDragging ? 1 : undefined,
      }}
      className={className}
    >
      {children({ attributes, listeners })}
    </div>
  );
}

/** The grip a row is dragged by. `touch-none` keeps a phone from scrolling the page instead. */
export function DragHandle({ handle, label }: { handle: DragHandleProps; label: string }) {
  return (
    <button
      type="button"
      aria-label={label}
      title="Drag to reorder"
      className="flex h-6 w-5 shrink-0 cursor-grab touch-none items-center justify-center rounded text-muted-foreground/60 hover:text-foreground active:cursor-grabbing focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      {...handle.attributes}
      {...handle.listeners}
    >
      <GripVertical className="h-4 w-4" />
    </button>
  );
}
