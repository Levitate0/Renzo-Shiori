"use client";

import { useState } from "react";
import { Check, ChevronDown, ChevronRight, ChevronUp, Folder, Pencil, Plus, Trash2, X } from "lucide-react";
import { toast } from "sonner";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ResponsiveModal } from "@/components/ui/responsive-modal";
import {
  useFavorites,
  useCreateFavoriteList,
  useRenameFavoriteList,
  useDeleteFavoriteList,
  useAddFavoriteItem,
  useRemoveFavoriteItem,
  useReorderFavoriteLists,
} from "@/lib/api/hooks/useFavorites";
import { type FavoriteList } from "@/lib/api/types";
import { DragHandle, SortableRow, SortableRows, type DragHandleProps } from "@/components/comp/sortable-rows";

/**
 * Folder button + management dialog for collections (the "favorites" lists
 * underneath — routes, types and storage keep that name, since installed Hub
 * builds call /api/favorites). The dialog is both the "add this series to
 * collections" picker (checkbox per collection) and the manager: create
 * top-level collections, create sub-collections under one, rename, reorder
 * and delete.
 */
export function FavoriteButton({ seriesId }: { seriesId: string }) {
  const [open, setOpen] = useState(false);
  const { data: lists } = useFavorites();

  const isFavorited = (lists ?? []).some((l) => l.seriesIds.includes(seriesId));

  return (
    <>
      <Button
        variant="outline"
        onClick={() => setOpen(true)}
        title={isFavorited ? "In your collections — manage" : "Add to a collection"}
        className={`px-0 w-9 sm:w-auto sm:px-4 ${
          isFavorited
            ? "border-pink-500/60 bg-pink-500/15 text-pink-500 hover:bg-pink-500/25 hover:text-pink-400 hover:border-pink-500/70"
            : ""
        }`}
      >
        <Folder className={`h-4 w-4 sm:mr-2 ${isFavorited ? "fill-current" : ""}`} />
        <span className="hidden sm:inline">{isFavorited ? "Collected" : "Collect"}</span>
      </Button>

      <ResponsiveModal
        open={open}
        onOpenChange={setOpen}
        title="Collections"
        description="Tick the collections this series belongs to. Create collections and sub-collections to organize, and drag them or use the arrows to reorder them."
      >
        <FavoritesPicker seriesId={seriesId} />
      </ResponsiveModal>
    </>
  );
}

function FavoritesPicker({ seriesId }: { seriesId: string }) {
  const { data: lists, isLoading } = useFavorites();
  const createList = useCreateFavoriteList();
  const reorder = useReorderFavoriteLists();

  const [newTabName, setNewTabName] = useState("");

  // sort() is stable, so equal sortOrders keep the server's order (it breaks
  // ties by creation time) — the same order the library dropdown shows.
  const topLevel = (lists ?? [])
    .filter((l) => !l.parentId)
    .sort((a, b) => a.sortOrder - b.sortOrder);
  const childrenOf = (id: string) =>
    (lists ?? [])
      .filter((l) => l.parentId === id)
      .sort((a, b) => a.sortOrder - b.sortOrder);

  // Saves a new order for one group of siblings: the top-level collections, or
  // one collection's sub-collections. A move never crosses groups. The whole
  // sibling order is sent, not just the pair that swapped — the server only
  // accepts the complete set, which is how it turns away a stale dialog.
  const save = async (parentId: string | null, ids: string[]) => {
    try {
      await reorder.mutateAsync({ parentId, ids });
    } catch (err) {
      // The hook has already put the old order back; the reason (e.g. the
      // collections changed on another device) rides along as the detail.
      toast.error("Failed to reorder collections", {
        description: err instanceof Error ? err.message : undefined,
      });
    }
  };

  // Moves one collection a step within its own siblings (the arrows).
  const move = (siblings: FavoriteList[], index: number, delta: -1 | 1) => {
    const moving = siblings[index];
    const target = index + delta;
    if (!moving || target < 0 || target >= siblings.length) return;
    const ids = siblings.filter((l) => l.id !== moving.id).map((l) => l.id);
    ids.splice(target, 0, moving.id);
    void save(moving.parentId ?? null, ids);
  };

  // undefined disables the arrow: the first row can't go up, the last can't go down.
  const moveHandlers = (siblings: FavoriteList[], index: number) => ({
    onMoveUp: index > 0 ? () => void move(siblings, index, -1) : undefined,
    onMoveDown: index < siblings.length - 1 ? () => void move(siblings, index, 1) : undefined,
  });

  const handleCreateTab = async () => {
    const name = newTabName.trim();
    if (!name) return;
    try {
      await createList.mutateAsync({ name });
      setNewTabName("");
    } catch (err) {
      toast.error(err instanceof Error ? err.message : "Failed to create collection");
    }
  };

  if (isLoading) {
    return <div className="py-8 text-center text-sm text-muted-foreground">Loading…</div>;
  }

  return (
    <div className="space-y-3 py-1">
      <div className="max-h-[50vh] space-y-1 overflow-y-auto pr-1">
        {topLevel.length === 0 && (
          <p className="py-4 text-center text-sm text-muted-foreground">
            No collections yet — create your first one below (e.g. “Manhwa”).
          </p>
        )}
        {/* A top-level row drags with its sub-collections; a sub-collection
            drags only among its parent's other children. */}
        <SortableRows ids={topLevel.map((l) => l.id)} onReorder={(ids) => void save(null, ids)} className="space-y-1">
          {topLevel.map((list, index) => {
            const children = childrenOf(list.id);
            return (
              <SortableRow key={list.id} id={list.id}>
                {(handle) => (
                  <>
                    <ListRow list={list} seriesId={seriesId} depth={0} handle={handle} {...moveHandlers(topLevel, index)} />
                    {children.length > 0 && (
                      <SortableRows ids={children.map((c) => c.id)} onReorder={(ids) => void save(list.id, ids)}>
                        {children.map((child, childIndex) => (
                          <SortableRow key={child.id} id={child.id}>
                            {(childHandle) => (
                              <ListRow
                                list={child}
                                seriesId={seriesId}
                                depth={1}
                                handle={childHandle}
                                {...moveHandlers(children, childIndex)}
                              />
                            )}
                          </SortableRow>
                        ))}
                      </SortableRows>
                    )}
                  </>
                )}
              </SortableRow>
            );
          })}
        </SortableRows>
      </div>

      {/* New top-level collection */}
      <div className="flex gap-2 border-t pt-3">
        <Input
          value={newTabName}
          onChange={(e) => setNewTabName(e.target.value)}
          placeholder="New collection, e.g. Manhwa #2"
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              void handleCreateTab();
            }
          }}
        />
        <Button
          type="button"
          variant="secondary"
          disabled={createList.isPending || !newTabName.trim()}
          onClick={() => void handleCreateTab()}
        >
          <Plus className="h-4 w-4 mr-1" />
          Collection
        </Button>
      </div>
    </div>
  );
}

function ListRow({
  list,
  seriesId,
  depth,
  handle,
  onMoveUp,
  onMoveDown,
}: {
  list: FavoriteList;
  seriesId: string;
  depth: number;
  handle: DragHandleProps;
  onMoveUp?: () => void;
  onMoveDown?: () => void;
}) {
  const addItem = useAddFavoriteItem();
  const removeItem = useRemoveFavoriteItem();
  const renameList = useRenameFavoriteList();
  const deleteList = useDeleteFavoriteList();
  const createList = useCreateFavoriteList();

  const [renaming, setRenaming] = useState(false);
  const [renameValue, setRenameValue] = useState(list.name);
  const [addingSub, setAddingSub] = useState(false);
  const [subName, setSubName] = useState("");

  const checked = list.seriesIds.includes(seriesId);

  const toggle = async () => {
    try {
      if (checked) {
        await removeItem.mutateAsync({ listId: list.id, seriesId });
      } else {
        await addItem.mutateAsync({ listId: list.id, seriesId });
      }
    } catch (err) {
      toast.error(err instanceof Error ? err.message : "Failed to update collections");
    }
  };

  const submitRename = async () => {
    const name = renameValue.trim();
    if (!name || name === list.name) {
      setRenaming(false);
      setRenameValue(list.name);
      return;
    }
    try {
      await renameList.mutateAsync({ id: list.id, name });
      setRenaming(false);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : "Failed to rename collection");
    }
  };

  const submitSub = async () => {
    const name = subName.trim();
    if (!name) return;
    try {
      await createList.mutateAsync({ name, parentId: list.id });
      setSubName("");
      setAddingSub(false);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : "Failed to create sub-collection");
    }
  };

  const handleDelete = async () => {
    if (!window.confirm(`Delete “${list.name}”${depth === 0 ? " and its sub-collections" : ""}? Series stay in your library.`)) return;
    try {
      await deleteList.mutateAsync(list.id);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : "Failed to delete collection");
    }
  };

  return (
    <div style={{ paddingLeft: depth * 22 }}>
      <div className="group flex items-center gap-2 rounded-md px-2 py-1.5 hover:bg-accent/50">
        <DragHandle handle={handle} label={`Drag to reorder ${list.name}`} />
        {depth > 0 && <ChevronRight className="h-3 w-3 shrink-0 text-muted-foreground/60" />}

        {/* Membership checkbox */}
        <button
          type="button"
          onClick={() => void toggle()}
          aria-label={checked ? `Remove from ${list.name}` : `Add to ${list.name}`}
          className={`flex h-[18px] w-[18px] shrink-0 items-center justify-center rounded border transition-colors ${
            checked
              ? "border-primary bg-primary text-primary-foreground"
              : "border-muted-foreground/40 hover:border-foreground/70"
          }`}
        >
          {checked && <Check className="h-3.5 w-3.5" />}
        </button>

        {renaming ? (
          <Input
            autoFocus
            value={renameValue}
            onChange={(e) => setRenameValue(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") { e.preventDefault(); void submitRename(); }
              if (e.key === "Escape") { setRenaming(false); setRenameValue(list.name); }
            }}
            onBlur={() => void submitRename()}
            className="h-7 flex-1 text-sm"
          />
        ) : (
          <button
            type="button"
            onClick={() => void toggle()}
            className="min-w-0 flex-1 truncate text-left text-sm"
          >
            {list.name}
            <span className="ml-1.5 text-[11px] text-muted-foreground/60 tabular-nums">
              {list.seriesIds.length}
            </span>
          </button>
        )}

        {/* Row actions — visible on hover/focus, and always on touch screens,
            which have no hover (a phone could never reach Delete otherwise). */}
        <div className="flex shrink-0 items-center gap-0.5 opacity-0 transition-opacity group-hover:opacity-100 group-focus-within:opacity-100 [@media(hover:none)]:opacity-100">
          {/* Disabled rather than hidden at the ends, so the arrows never
              shift under a finger that is pressing one repeatedly. */}
          <IconButton title="Move up" onClick={onMoveUp}>
            <ChevronUp className="h-3.5 w-3.5" />
          </IconButton>
          <IconButton title="Move down" onClick={onMoveDown}>
            <ChevronDown className="h-3.5 w-3.5" />
          </IconButton>
          {depth === 0 && (
            <IconButton title="Add sub-collection" onClick={() => setAddingSub((v) => !v)}>
              {addingSub ? <X className="h-3.5 w-3.5" /> : <Plus className="h-3.5 w-3.5" />}
            </IconButton>
          )}
          <IconButton title="Rename" onClick={() => { setRenaming(true); setRenameValue(list.name); }}>
            <Pencil className="h-3.5 w-3.5" />
          </IconButton>
          <IconButton title="Delete collection" onClick={() => void handleDelete()}>
            <Trash2 className="h-3.5 w-3.5 text-destructive" />
          </IconButton>
        </div>
      </div>

      {/* Inline sub-collection creation */}
      {addingSub && (
        <div className="flex gap-2 py-1 pl-8 pr-2">
          <Input
            autoFocus
            value={subName}
            onChange={(e) => setSubName(e.target.value)}
            placeholder="Sub-collection name, e.g. an alt name"
            onKeyDown={(e) => {
              if (e.key === "Enter") { e.preventDefault(); void submitSub(); }
              if (e.key === "Escape") { setAddingSub(false); setSubName(""); }
            }}
            className="h-7 text-sm"
          />
          <Button type="button" size="sm" variant="secondary" className="h-7" disabled={!subName.trim()} onClick={() => void submitSub()}>
            Add
          </Button>
        </div>
      )}
    </div>
  );
}

/** A row action. No onClick renders it disabled (e.g. Move up on the first row). */
function IconButton({ title, onClick, children }: { title: string; onClick?: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      title={title}
      onClick={onClick}
      disabled={!onClick}
      className="flex h-6 w-6 items-center justify-center rounded text-muted-foreground transition-colors hover:bg-foreground/10 hover:text-foreground disabled:pointer-events-none disabled:opacity-30"
    >
      {children}
    </button>
  );
}
