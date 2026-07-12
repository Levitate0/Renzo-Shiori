"use client";

import { useState } from "react";
import { Check, ChevronRight, Heart, Pencil, Plus, Trash2, X } from "lucide-react";
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
} from "@/lib/api/hooks/useFavorites";
import { type FavoriteList } from "@/lib/api/types";

/**
 * Heart button + management dialog for the favorites system. The dialog is
 * both the "add this series to lists" picker (checkbox per list) and the
 * list manager: create new top-level tabs, create sub-lists under a tab,
 * rename, and delete.
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
        title={isFavorited ? "In your favourites — manage lists" : "Add to favourites"}
        className={`px-0 w-9 sm:w-auto sm:px-4 ${
          isFavorited
            ? "border-pink-500/60 bg-pink-500/15 text-pink-500 hover:bg-pink-500/25 hover:text-pink-400 hover:border-pink-500/70"
            : ""
        }`}
      >
        <Heart className={`h-4 w-4 sm:mr-2 ${isFavorited ? "fill-current" : ""}`} />
        <span className="hidden sm:inline">{isFavorited ? "Favourited" : "Favourite"}</span>
      </Button>

      <ResponsiveModal
        open={open}
        onOpenChange={setOpen}
        title="Favourites"
        description="Tick the lists this series belongs to. Create tabs and sub-lists to organize."
      >
        <FavoritesPicker seriesId={seriesId} />
      </ResponsiveModal>
    </>
  );
}

function FavoritesPicker({ seriesId }: { seriesId: string }) {
  const { data: lists, isLoading } = useFavorites();
  const createList = useCreateFavoriteList();

  const [newTabName, setNewTabName] = useState("");

  const topLevel = (lists ?? [])
    .filter((l) => !l.parentId)
    .sort((a, b) => a.sortOrder - b.sortOrder);
  const childrenOf = (id: string) =>
    (lists ?? [])
      .filter((l) => l.parentId === id)
      .sort((a, b) => a.sortOrder - b.sortOrder);

  const handleCreateTab = async () => {
    const name = newTabName.trim();
    if (!name) return;
    try {
      await createList.mutateAsync({ name });
      setNewTabName("");
    } catch (err) {
      toast.error(err instanceof Error ? err.message : "Failed to create list");
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
            No favourites lists yet — create your first tab below (e.g. “Manhwa favourites”).
          </p>
        )}
        {topLevel.map((list) => (
          <div key={list.id}>
            <ListRow list={list} seriesId={seriesId} depth={0} />
            {childrenOf(list.id).map((child) => (
              <ListRow key={child.id} list={child} seriesId={seriesId} depth={1} />
            ))}
          </div>
        ))}
      </div>

      {/* New top-level tab */}
      <div className="flex gap-2 border-t pt-3">
        <Input
          value={newTabName}
          onChange={(e) => setNewTabName(e.target.value)}
          placeholder="New tab, e.g. Manhwa favourites #2"
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
          Tab
        </Button>
      </div>
    </div>
  );
}

function ListRow({ list, seriesId, depth }: { list: FavoriteList; seriesId: string; depth: number }) {
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
      toast.error(err instanceof Error ? err.message : "Failed to update favourites");
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
      toast.error(err instanceof Error ? err.message : "Failed to rename list");
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
      toast.error(err instanceof Error ? err.message : "Failed to create sub-list");
    }
  };

  const handleDelete = async () => {
    if (!window.confirm(`Delete “${list.name}”${depth === 0 ? " and its sub-lists" : ""}? Series stay in your library.`)) return;
    try {
      await deleteList.mutateAsync(list.id);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : "Failed to delete list");
    }
  };

  return (
    <div style={{ paddingLeft: depth * 22 }}>
      <div className="group flex items-center gap-2 rounded-md px-2 py-1.5 hover:bg-accent/50">
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

        {/* Row actions — visible on hover/focus */}
        <div className="flex shrink-0 items-center gap-0.5 opacity-0 transition-opacity group-hover:opacity-100 group-focus-within:opacity-100">
          {depth === 0 && (
            <IconButton title="Add sub-list" onClick={() => setAddingSub((v) => !v)}>
              {addingSub ? <X className="h-3.5 w-3.5" /> : <Plus className="h-3.5 w-3.5" />}
            </IconButton>
          )}
          <IconButton title="Rename" onClick={() => { setRenaming(true); setRenameValue(list.name); }}>
            <Pencil className="h-3.5 w-3.5" />
          </IconButton>
          <IconButton title="Delete list" onClick={() => void handleDelete()}>
            <Trash2 className="h-3.5 w-3.5 text-destructive" />
          </IconButton>
        </div>
      </div>

      {/* Inline sub-list creation */}
      {addingSub && (
        <div className="flex gap-2 py-1 pl-8 pr-2">
          <Input
            autoFocus
            value={subName}
            onChange={(e) => setSubName(e.target.value)}
            placeholder="Sub-list name, e.g. an alt name"
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

function IconButton({ title, onClick, children }: { title: string; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      title={title}
      onClick={onClick}
      className="flex h-6 w-6 items-center justify-center rounded text-muted-foreground transition-colors hover:bg-foreground/10 hover:text-foreground"
    >
      {children}
    </button>
  );
}
