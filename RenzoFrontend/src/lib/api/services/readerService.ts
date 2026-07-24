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
  // refresh=true bypasses the backend page-list cache — used to re-check a locked
  // chapter after it may have been purchased or turned free.
  async streamPages(seriesId: string, chapterNumber: number, refresh = false): Promise<{ pageCount: number; locked?: boolean }> {
    return apiClient.get<{ pageCount: number; locked?: boolean }>(
      `/api/reader/stream/pages?seriesId=${seriesId}&chapter=${chapterNumber}${refresh ? "&refresh=true" : ""}`);
  },

  streamPageUrl(seriesId: string, chapterNumber: number, page: number): string {
    return formatThumbnailUrl(
      `/api/reader/stream/page?seriesId=${seriesId}&chapter=${chapterNumber}&page=${page}`);
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

  /** Clear the in-memory cache of streamed (web-pulled) page images. */
  async clearStreamCache(): Promise<{ success: boolean; cleared: number }> {
    return apiClient.post<{ success: boolean; cleared: number }>('/api/reader/clear-stream-cache', {});
  },
};
