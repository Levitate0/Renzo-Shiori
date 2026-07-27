"use client";
import * as React from "react";
import { readerService } from "@/lib/api/services/readerService";
import { nativePrimitives } from "./bridge";

/**
 * Read-status sync for offline reading. The bundled offline reader records the
 * chapters you finish (in the `renzo.offline.pendingReads` KV list). When the
 * app is online again, this pushes them to the server (marks them read) and
 * clears what succeeded — so progress you made offline shows up everywhere.
 */
const PENDING_KEY = "renzo.offline.pendingReads";

interface PendingRead {
  seriesId: string;
  chapterNumber: number;
}

export async function flushPendingReads(): Promise<void> {
  const nat = nativePrimitives();
  if (!nat) return;
  const raw = await nat.kvGet(PENDING_KEY);
  if (!raw) return;
  let pending: PendingRead[];
  try {
    pending = JSON.parse(raw) as PendingRead[];
  } catch {
    return;
  }
  if (!Array.isArray(pending) || pending.length === 0) return;

  const bySeries = new Map<string, number[]>();
  for (const r of pending) {
    if (!r?.seriesId || typeof r.chapterNumber !== "number") continue;
    const arr = bySeries.get(r.seriesId) ?? [];
    if (!arr.includes(r.chapterNumber)) arr.push(r.chapterNumber);
    bySeries.set(r.seriesId, arr);
  }

  const failed: PendingRead[] = [];
  for (const [seriesId, nums] of bySeries) {
    try {
      await readerService.markChapters(seriesId, nums, true);
    } catch {
      for (const n of nums) failed.push({ seriesId, chapterNumber: n });
    }
  }
  await nat.kvSet(PENDING_KEY, JSON.stringify(failed));
}

/** Flush offline reads on mount and whenever connectivity returns. No-op on web. */
export function useOfflineReadSync(): void {
  React.useEffect(() => {
    const nat = nativePrimitives();
    if (!nat) return;
    void flushPendingReads();
    return nat.onNetworkChange((online) => {
      if (online) void flushPendingReads();
    });
  }, []);
}
