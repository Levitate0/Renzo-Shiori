"use client";
import * as React from "react";
import { getManifest } from "./offline";

/**
 * App-wide live status of native offline downloads. A single window listener
 * (set up once) tracks the `renzo:download` broadcasts so progress survives
 * leaving the series page — the download runs natively and this reflects it
 * anywhere in the app. Series titles are resolved from the manifest (the native
 * downloader writes series metadata at job start).
 */
export interface OfflineDownloadStatus {
  series: Record<string, { title: string; done: number; total: number }>;
  active: boolean;
}

const EMPTY: OfflineDownloadStatus = { series: {}, active: false };
let state: OfflineDownloadStatus = EMPTY;
const listeners = new Set<() => void>();
let initialized = false;

function emit() {
  for (const l of listeners) l();
}

function resolveTitle(seriesId: string): void {
  void getManifest().then((m) => {
    const title = m.series[seriesId]?.title;
    const cur = state.series[seriesId];
    if (title && cur && cur.title !== title) {
      state = { ...state, series: { ...state.series, [seriesId]: { ...cur, title } } };
      emit();
    }
  });
}

function init() {
  if (initialized || typeof window === "undefined") return;
  initialized = true;
  window.addEventListener("renzo:download", (e: Event) => {
    const d = (e as CustomEvent<{ state?: string; seriesId?: string; done?: number; total?: number }>).detail;
    if (!d) return;
    if (d.state === "idle") {
      state = EMPTY;
      emit();
      return;
    }
    if (!d.seriesId) return;
    const prev = state.series[d.seriesId] ?? { title: "", done: 0, total: 0 };
    state = {
      active: true,
      series: {
        ...state.series,
        [d.seriesId]: { title: prev.title, done: d.done ?? prev.done, total: d.total ?? prev.total },
      },
    };
    emit();
    if (!prev.title) resolveTitle(d.seriesId);
  });
}

function subscribe(cb: () => void): () => void {
  init();
  listeners.add(cb);
  return () => {
    listeners.delete(cb);
  };
}

export function useOfflineDownloadStatus(): OfflineDownloadStatus {
  return React.useSyncExternalStore(
    subscribe,
    () => state,
    () => EMPTY,
  );
}
