"use client";
import * as React from "react";
import Link from "next/link";
import { ArrowLeft, FolderOpen, Trash2, HardDrive, WifiOff } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { useToast } from "@/hooks/use-toast";
import { useIsNative, useOfflineDownloads } from "@/lib/native/hooks";
import { nativePrimitives } from "@/lib/native/bridge";
import {
  autoPurgeEnabled,
  setAutoPurge,
  deleteOffline,
  purgeAll,
  offlineBytes,
} from "@/lib/native/offline";

function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`;
  const units = ["KB", "MB", "GB"];
  let v = n / 1024;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(v < 10 ? 1 : 0)} ${units[i]}`;
}

export default function DownloadsPage() {
  const isNative = useIsNative();
  const { toast } = useToast();
  const { items, loading, refresh } = useOfflineDownloads();
  const [folder, setFolder] = React.useState<string | null>(null);
  const [bytes, setBytes] = React.useState(0);
  const [autoPurge, setAutoPurgeState] = React.useState(true);

  const reload = React.useCallback(async () => {
    setBytes(await offlineBytes());
    const nat = nativePrimitives();
    if (nat) setFolder(await nat.getFolder());
    setAutoPurgeState(autoPurgeEnabled());
  }, []);

  React.useEffect(() => {
    if (isNative) void reload();
  }, [isNative, reload, items]);

  if (!isNative) {
    return (
      <div className="mx-auto max-w-2xl px-4 py-16 text-center">
        <WifiOff className="mx-auto h-10 w-10 text-muted-foreground/50" />
        <h1 className="mt-4 text-lg font-semibold">Offline downloads</h1>
        <p className="mt-2 text-sm text-muted-foreground">
          Saving chapters for offline reading is available in the Renzo Shiori app (Android/desktop).
        </p>
      </div>
    );
  }

  const pickFolder = async () => {
    const nat = nativePrimitives();
    if (!nat) return;
    const chosen = await nat.pickFolder();
    if (chosen) {
      setFolder(chosen);
      toast({ title: "Download folder set", description: chosen });
    }
  };

  const removeOne = async (key: string, n: number) => {
    await deleteOffline(key);
    toast({ title: "Removed", description: `Chapter ${n} deleted from device.` });
    await refresh();
  };

  const removeAll = async () => {
    const purged = await purgeAll();
    toast({ title: "Cleared", description: `${purged} chapter${purged === 1 ? "" : "s"} removed.` });
    await refresh();
  };

  return (
    <div className="mx-auto max-w-3xl px-4 py-8">
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-2 min-w-0">
          <Link
            href="/library"
            aria-label="Back to library"
            className="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-md border text-muted-foreground hover:bg-accent hover:text-foreground"
          >
            <ArrowLeft className="h-4 w-4" />
          </Link>
          <div className="min-w-0">
          <h1 className="text-xl font-semibold">Offline downloads</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            {items.length} chapter{items.length === 1 ? "" : "s"} · {formatBytes(bytes)} on device
          </p>
          </div>
        </div>
        {items.length > 0 && (
          <Button variant="outline" size="sm" onClick={() => void removeAll()} className="gap-1.5">
            <Trash2 className="h-4 w-4" /> Clear all
          </Button>
        )}
      </div>

      {/* Settings */}
      <div className="mt-6 space-y-3 rounded-lg border p-4">
        <div className="flex items-center justify-between gap-3">
          <div className="min-w-0">
            <div className="flex items-center gap-2 text-sm font-medium">
              <FolderOpen className="h-4 w-4" /> Download folder
            </div>
            <p className="mt-0.5 truncate text-xs text-muted-foreground">
              {folder ?? "App default (private storage)"}
            </p>
          </div>
          <Button variant="outline" size="sm" onClick={() => void pickFolder()}>
            Choose…
          </Button>
        </div>
        <div className="flex items-center justify-between gap-3 border-t pt-3">
          <div>
            <div className="flex items-center gap-2 text-sm font-medium">
              <HardDrive className="h-4 w-4" /> Auto-clean on reconnect
            </div>
            <p className="mt-0.5 text-xs text-muted-foreground">
              When you&apos;re back online, ask to remove trip downloads (never the chapter you&apos;re reading).
            </p>
          </div>
          <Switch
            checked={autoPurge}
            onCheckedChange={(v) => {
              setAutoPurge(v);
              setAutoPurgeState(v);
            }}
          />
        </div>
      </div>

      {/* List */}
      <div className="mt-6">
        {loading ? (
          <p className="py-8 text-center text-sm text-muted-foreground">Loading…</p>
        ) : items.length === 0 ? (
          <p className="py-12 text-center text-sm text-muted-foreground">
            No downloads yet. Open a series and tap <span className="font-medium">Save offline</span> on a chapter.
          </p>
        ) : (
          <ul className="space-y-2">
            {items.map((c) => (
              <li key={c.chapterKey} className="flex items-center gap-3 rounded-lg border bg-card/50 px-3 py-2.5">
                <div className="min-w-0 flex-1">
                  <div className="truncate text-sm font-medium">{c.seriesTitle}</div>
                  <div className="text-xs text-muted-foreground">
                    Ch. {c.chapterNumber} · {c.pageCount} pages · {formatBytes(c.bytes)}
                  </div>
                </div>
                <Button
                  variant="ghost"
                  size="icon"
                  className="h-8 w-8 text-muted-foreground hover:text-destructive"
                  onClick={() => void removeOne(c.chapterKey, c.chapterNumber)}
                  aria-label={`Remove chapter ${c.chapterNumber}`}
                >
                  <Trash2 className="h-4 w-4" />
                </Button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}
