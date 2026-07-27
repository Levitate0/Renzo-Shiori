"use client";
import * as React from "react";
import { isNative, nativePlatform, nativePrimitives } from "./bridge";
import { autoPurgeEnabled, listOffline, purgeAll } from "./offline";
import { getCurrentReadingChapter } from "./current-read";
import type { NativePlatform, OfflineChapter } from "./types";

/** SSR-safe platform read (returns "web" until mounted). */
export function useNativePlatform(): NativePlatform {
  const [platform, setPlatform] = React.useState<NativePlatform>("web");
  React.useEffect(() => setPlatform(nativePlatform()), []);
  return platform;
}

export function useIsNative(): boolean {
  return useNativePlatform() !== "web";
}

/**
 * Mount once near the app root. When connectivity is restored, and auto-purge
 * is on, deletes downloaded chapters — sparing the one the reader currently has
 * open (via the current-read module). No-op on web / when nothing is downloaded.
 */
export function useOfflinePurgeWatcher(): void {
  React.useEffect(() => {
    const nat = nativePrimitives();
    if (!nat) return;
    let wasOnline: boolean | null = null;
    void nat.isOnline().then((o) => {
      if (wasOnline === null) wasOnline = o;
    });
    return nat.onNetworkChange((online) => {
      if (online && wasOnline === false && autoPurgeEnabled()) {
        void purgeAll(getCurrentReadingChapter());
      }
      wasOnline = online;
    });
  }, []);
}

/** Live list of downloaded chapters for a management view. */
export function useOfflineDownloads(): {
  items: OfflineChapter[];
  loading: boolean;
  refresh: () => Promise<void>;
} {
  const [items, setItems] = React.useState<OfflineChapter[]>([]);
  const [loading, setLoading] = React.useState(true);

  const refresh = React.useCallback(async () => {
    if (!isNative()) {
      setItems([]);
      setLoading(false);
      return;
    }
    setItems(await listOffline());
    setLoading(false);
  }, []);

  React.useEffect(() => {
    void refresh();
  }, [refresh]);

  return { items, loading, refresh };
}
