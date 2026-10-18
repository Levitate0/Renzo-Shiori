import { useEffect, useSyncExternalStore } from 'react';
import { getProgressHub } from '../signalr/progressHub';
import { JobType, ProgressStatus } from '../types';
import type { ProgressState, DownloadCardInfo } from '../types';

// useQueue / useRemoveFromQueue / useClearQueue lived here and were backed by a
// MockQueueService that only ever mutated an empty in-memory array — no HTTP
// call at all. They are gone rather than reimplemented: the queue page reads its
// lists from the downloads API and removes entries through
// useManageErrorDownload, which is the same endpoint Retry already used.

export interface DownloadProgress {
  id: string;
  cardInfo: DownloadCardInfo;
  percentage: number;
  message: string;
  status: ProgressStatus;
  errorMessage?: string;
}

/*
 * The live download list, kept once for the whole app rather than per
 * component. Each consumer (the top-bar downloads panel, the queue page) used
 * to build its own copy from the events it saw after it mounted, so changing
 * page started from an empty list, and nothing removed a download whose end
 * was missed — one that finished during a reconnect or a server restart stayed
 * "in progress" until the page was reloaded. The server now sends the whole
 * list on every (re)connect ("ActiveDownloads"), and that replaces this one.
 */
type DownloadMap = Record<string, DownloadProgress>;

const EMPTY: DownloadProgress[] = [];
let current: DownloadMap = {};
let currentList: DownloadProgress[] = EMPTY;
const subscribers = new Set<() => void>();
let stopFeed: (() => void) | null = null;

function publish(next: DownloadMap): void {
  current = next;
  currentList = Object.values(next);
  subscribers.forEach((notify) => notify());
}

function isFinished(status: ProgressStatus): boolean {
  return status === ProgressStatus.Completed || status === ProgressStatus.Failed;
}

function toEntry(progress: ProgressState, cardInfo: DownloadCardInfo): DownloadProgress {
  return {
    id: progress.id,
    cardInfo,
    percentage: progress.percentage,
    message: progress.message,
    status: progress.progressStatus,
    errorMessage: progress.errorMessage,
  };
}

function applyProgress(progress: ProgressState): void {
  if (progress.jobType !== JobType.Download) return;
  // A completed or failed download leaves the list.
  if (isFinished(progress.progressStatus)) {
    if (!(progress.id in current)) return;
    const { [progress.id]: _finished, ...rest } = current;
    publish(rest);
    return;
  }
  // Nothing to show until an event carries the download's card.
  const cardInfo = current[progress.id]?.cardInfo ?? progress.download;
  if (!cardInfo) return;
  publish({ ...current, [progress.id]: toEntry(progress, cardInfo) });
}

function applySnapshot(states: ProgressState[] | null | undefined): void {
  const next: DownloadMap = {};
  for (const state of states ?? []) {
    if (state.jobType !== JobType.Download || !state.download || isFinished(state.progressStatus)) continue;
    next[state.id] = toEntry(state, state.download);
  }
  publish(next);
}

function startFeed(): void {
  if (stopFeed || typeof window === 'undefined') return;
  const hub = getProgressHub();
  const offProgress = hub.onProgress(applyProgress);
  const offSnapshot = hub.onActiveDownloads(applySnapshot);
  stopFeed = () => {
    offProgress();
    offSnapshot();
  };
  hub.startConnection().catch((error: unknown) => {
    console.error('Failed to setup SignalR connection for downloads:', error);
  });
  // The client's own reconnect gives up after four tries; this restarts it
  // every 30 s while the tab is visible, so the list never goes quiet for good.
  hub.startHealthCheck();
}

/**
 * Signing out: the connection is authenticated as the account that just left,
 * so it is closed and the list emptied. The next sign-in opens a new one.
 */
export async function stopDownloadFeed(): Promise<void> {
  stopFeed?.();
  stopFeed = null;
  publish({});
  const hub = getProgressHub();
  hub.stopHealthCheck();
  await hub.stopConnection();
}

function subscribe(notify: () => void): () => void {
  subscribers.add(notify);
  return () => {
    subscribers.delete(notify);
  };
}

const getSnapshot = (): DownloadProgress[] => currentList;
const getServerSnapshot = (): DownloadProgress[] => EMPTY;

// Hook for live download progress via SignalR
export function useDownloadProgress() {
  useEffect(startFeed, []);
  const downloads = useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);
  return {
    downloads,
    downloadCount: downloads.length,
  };
}
