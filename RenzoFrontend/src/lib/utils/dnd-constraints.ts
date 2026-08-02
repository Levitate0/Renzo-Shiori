import type { Modifier } from "@dnd-kit/core";

/**
 * Drag constraints for the priority-reorder lists.
 *
 * These are the two modifiers @dnd-kit ships in its optional `modifiers`
 * package, inlined so the app doesn't take another dependency for ~20 lines.
 *
 * Without them a dragged source card follows the pointer freely: it drifts
 * sideways out of its column, overlaps the neighbouring panel, and pushes the
 * page wide enough to raise a horizontal scrollbar mid-drag. The lists are
 * vertical, so sideways travel conveys nothing anyway.
 */

/** Locks movement to the Y axis — the list only ever reorders vertically. */
export const restrictToVerticalAxis: Modifier = ({ transform }) => ({
  ...transform,
  x: 0,
});

/**
 * Keeps the dragged card inside its own container, so it can't be pulled above
 * the first row, below the last, or out over another panel.
 */
export const restrictToParentElement: Modifier = ({
  containerNodeRect,
  draggingNodeRect,
  transform,
}) => {
  if (!draggingNodeRect || !containerNodeRect) return transform;

  const value = { ...transform };

  if (draggingNodeRect.top + value.y <= containerNodeRect.top) {
    value.y = containerNodeRect.top - draggingNodeRect.top;
  } else if (
    draggingNodeRect.bottom + value.y >=
    containerNodeRect.top + containerNodeRect.height
  ) {
    value.y =
      containerNodeRect.top + containerNodeRect.height - draggingNodeRect.bottom;
  }

  if (draggingNodeRect.left + value.x <= containerNodeRect.left) {
    value.x = containerNodeRect.left - draggingNodeRect.left;
  } else if (
    draggingNodeRect.right + value.x >=
    containerNodeRect.left + containerNodeRect.width
  ) {
    value.x =
      containerNodeRect.left + containerNodeRect.width - draggingNodeRect.right;
  }

  return value;
};

/** Both constraints, in the order a vertical sortable list wants them. */
export const verticalSortableConstraints: Modifier[] = [
  restrictToVerticalAxis,
  restrictToParentElement,
];
