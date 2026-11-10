import { apiClient } from '@/lib/api/client';
import { formatThumbnailUrl } from '@/lib/utils/thumbnail';
import {
  type ReaderChapters,
  type ReaderChapterInfo,
  type PreviewChapters,
  type BackupImportResult,
} from '@/lib/api/types';

/** Base64url — chapter filenames can contain characters that break query strings. */
export function encodeFilename(filename: string): string {
  return btoa(unescape(encodeURIComponent(filename)))
    .replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** A streamed chapter's page count, plus which source served it and how many could. */
export interface StreamPages {
  pageCount: number;
  locked?: boolean;
  source?: string | null;
  sourceCount?: number;
}

export const readerService = {
  async getChapters(seriesId: string): Promise<ReaderChapters> {
    return apiClient.get<ReaderChapters>(`/api/reader/chapters?seriesId=${seriesId}`);
  },

  async getChapterInfo(seriesId: string, filename: string): Promise<ReaderChapterInfo> {
    return apiClient.get<ReaderChapterInfo>(
      `/api/reader/chapter-info?seriesId=${seriesId}&filename=${encodeFilename(filename)}`);
  },

  /** Authenticated <img> URL for a library page. */
  pageUrl(seriesId: string, filename: string, page: number): string {
    return formatThumbnailUrl(`/api/reader/page?seriesId=${seriesId}&filename=${encodeFilename(filename)}&page=${page}`);
  },

  async setProgress(seriesId: string, chapterNumber: number, lastReadPage: number, totalPages: number, filename?: string): Promise<void> {
    return apiClient.post<void>('/api/reader/progress', { seriesId, chapterNumber, lastReadPage, totalPages, filename });
  },

  async markChapters(seriesId: string, chapterNumbers: number[], read: boolean): Promise<void> {
    return apiClient.post<void>('/api/reader/mark', { seriesId, chapterNumbers, read });
  },

  async setBookmark(seriesId: string, chapterNumber: number, bookmarked: boolean): Promise<void> {
    return apiClient.post<void>('/api/reader/bookmark', { seriesId, chapterNumber, bookmarked });
  },

  // ── Library streaming (read a not-yet-downloaded chapter live) ──
  // refresh=true bypasses the backend page-list cache and drops the chapter's
  // cached page images, so the pages genuinely come from the source again. Used
  // when opening an undownloaded chapter — it may have gained or fixed pages
  // since it was last read.
  //
  // refreshChapters=true additionally re-fetches the source's whole chapter
  // list. That is one 20s-bounded source call PER candidate source, paid before
  // the reader can open, so it is reserved for the locked-chapter poll, where
  // "has this chapter appeared in the listing yet" is the actual question. A
  // plain reopen must never set it.
  async streamPages(
    seriesId: string,
    chapterNumber: number,
    refresh = false,
    refreshChapters = false,
    // "Switch source": reload from the next source instead of the one that served it.
    nextSource = false,
  ): Promise<StreamPages> {
    return apiClient.get<StreamPages>(
      `/api/reader/stream/pages?seriesId=${seriesId}&chapter=${chapterNumber}`
      + `${refresh ? "&refresh=true" : ""}${refreshChapters ? "&refreshChapters=true" : ""}`
      + `${nextSource ? "&nextSource=true" : ""}`);
  },

  // Auto-buy of a paid chapter the reader has reached (on screen, never a
  // prefetch). Runs on the server in the background: start, then poll.
  async startAutoBuy(seriesId: string, chapterNumber: number): Promise<AutoBuyResult> {
    return apiClient.post<AutoBuyResult>(
      `/api/reader/auto-buy?seriesId=${seriesId}&chapter=${chapterNumber}`, {});
  },

  async autoBuyStatus(seriesId: string, chapterNumber: number): Promise<AutoBuyResult> {
    return apiClient.get<AutoBuyResult>(
      `/api/reader/auto-buy?seriesId=${seriesId}&chapter=${chapterNumber}`);
  },

  // `version` changes the URL after "Switch source", so the browser's cached
  // images from the old source (kept a day) are not shown for the new one.
  streamPageUrl(seriesId: string, chapterNumber: number, page: number, version = 0): string {
    return formatThumbnailUrl(
      `/api/reader/stream/page?seriesId=${seriesId}&chapter=${chapterNumber}&page=${page}`
      + `${version ? `&v=${version}` : ""}`);
  },

  // ── Preview (Browse items, nothing stored) ──
  async getPreviewChapters(mihonId: string): Promise<PreviewChapters> {
    return apiClient.get<PreviewChapters>(`/api/reader/preview/chapters?mihonId=${encodeURIComponent(mihonId)}`);
  },

  async getPreviewPages(mihonId: string, chapter: number): Promise<{ pageCount: number }> {
    return apiClient.get<{ pageCount: number }>(
      `/api/reader/preview/pages?mihonId=${encodeURIComponent(mihonId)}&chapter=${chapter}`);
  },

  previewPageUrl(mihonId: string, chapter: number, page: number): string {
    return formatThumbnailUrl(`/api/reader/preview/page?mihonId=${encodeURIComponent(mihonId)}&chapter=${chapter}&page=${page}`);
  },

  async importBackup(file: File): Promise<BackupImportResult> {
    const form = new FormData();
    form.append('file', file);
    return apiClient.post<BackupImportResult>('/api/reader/import-backup', form);
  },

  /** Keep only these chapters' streamed page images for a series (paged reader). */
  async trimStreamCache(seriesId: string, keep: number[]): Promise<{ success: boolean; cleared: number }> {
    return apiClient.post<{ success: boolean; cleared: number }>('/api/reader/trim-stream-cache', { seriesId, keep });
  },

  /** Clear the in-memory cache of streamed (web-pulled) page images. */
  async clearStreamCache(): Promise<{ success: boolean; cleared: number }> {
    return apiClient.post<{ success: boolean; cleared: number }>('/api/reader/clear-stream-cache', {});
  },
};

/** Answer of /api/reader/auto-buy. */
export interface AutoBuyResult {
  status: "running" | "bought" | "failed" | "off" | "dry-run";
  /** On failure: no-login | signed-out | no-coins | no-button | not-confirmed | not-owner | unavailable. */
  reason?: string | null;
  /** Why it couldn't buy, for the purchase screen. */
  message?: string | null;
  provider?: string | null;
  price?: number | null;
  balance?: number | null;
}
