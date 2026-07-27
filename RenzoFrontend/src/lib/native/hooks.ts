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
 * Reconnect purge, **confirmation-gated**. When connectivity is restored and
 * auto-purge is enabled, this surfaces a prompt (count of purgeable chapters)
 * instead of deleting anything — so a brief signal blip on a train can't silently
 * wipe your trip downloads. The chapter currently open in the reader is always
 * excluded from the count and the purge. No-op on web / when nothing is saved.
 *
 * Mount once near the app root; render the prompt from the returned state.
 */
export function useReconnectPurge(): {
  pending: boolean;
  count: number;
  confirm: () => Promise<void>;
  dismiss: () => void;
} {
  const [pending, setPending] = React.useState(false);
  const [count, setCount] = React.useState(0);

  React.useEffect(() => {
    const nat = nativePrimitives();
    if (!nat) return;
    let wasOnline: boolean | null = null;
    void nat.isOnline().then((o) => {
      if (wasOnline === null) wasOnline = o;
    });
    return nat.onNetworkChange((online) => {
      const prev = wasOnline;
      wasOnline = online;
      if (!(online && prev === false && autoPurgeEnabled())) return;
      void listOffline().then((items) => {
        const current = getCurrentReadingChapter();
        const purgeable = items.filter((c) => c.chapterKey !== current).length;
        if (purgeable > 0) {
          setCount(purgeable);
          setPending(true);
        }
      });
    });
  }, []);

  const confirm = React.useCallback(async () => {
    await purgeAll(getCurrentReadingChapter());
    setPending(false);
  }, []);
  const dismiss = React.useCallback(() => setPending(false), []);

  return { pending, count, confirm, dismiss };
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
