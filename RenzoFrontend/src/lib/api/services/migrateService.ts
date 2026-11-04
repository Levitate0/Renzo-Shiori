import { apiClient } from '@/lib/api/client';
import {
  type MigrationApplyRequest,
  type MigrationRun,
  type MigrationSources,
  type MigrationStartRequest,
} from '@/lib/api/types';

/**
 * Source migration: move every series on a broken source onto others
 * (server: Controllers/MigrateController.cs). Used by the Sources page's
 * Migrate tab (components/comp/sources/migrate-tab.tsx).
 * Hub twin: data/network/SourcesApi.kt (migration calls).
 */
export const migrateService = {
  /** The sources the user's series use, and the ones they can move to. */
  async sources(): Promise<MigrationSources> {
    return apiClient.get<MigrationSources>('/api/migrate/sources');
  },

  /** The user's latest migration, or null when there is none (204). */
  async current(): Promise<MigrationRun | null> {
    const run = await apiClient.get<MigrationRun | undefined>('/api/migrate/current');
    return run ?? null;
  },

  /** Starts looking for matches; 409 carries the server's reason. */
  async start(request: MigrationStartRequest): Promise<MigrationRun> {
    return apiClient.post<MigrationRun>('/api/migrate', request);
  },

  async get(id: string): Promise<MigrationRun> {
    return apiClient.get<MigrationRun>(`/api/migrate/${encodeURIComponent(id)}`);
  },

  /** Adds the chosen matches; 409 carries the server's reason. */
  async apply(id: string, request: MigrationApplyRequest): Promise<MigrationRun> {
    return apiClient.post<MigrationRun>(`/api/migrate/${encodeURIComponent(id)}/apply`, request);
  },

  /** Forgets a finished migration. */
  async discard(id: string): Promise<void> {
    return apiClient.delete<void>(`/api/migrate/${encodeURIComponent(id)}`);
  },
};
