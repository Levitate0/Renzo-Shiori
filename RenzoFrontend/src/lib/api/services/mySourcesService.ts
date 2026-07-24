import { apiClient } from '@/lib/api/client';

export interface AllSourceRow {
  mihonProviderId: string;
  provider: string;
  scanlator: string;
  language: string;
  enabled: boolean;
}

/**
 * Per-user source visibility: every source is installed once, shared by the
 * whole instance, but each user decides which of the installed sources show
 * up in their own Search/Browse/Add-series. Installing a source doesn't make
 * it visible to anyone else — they opt in separately.
 */
export const mySourcesService = {
  async listAll(): Promise<AllSourceRow[]> {
    return apiClient.get<AllSourceRow[]>('/api/provider/all-sources');
  },

  async enable(mihonProviderId: string): Promise<void> {
    return apiClient.post<void>(`/api/provider/my-sources/${encodeURIComponent(mihonProviderId)}`, {});
  },

  async disable(mihonProviderId: string): Promise<void> {
    return apiClient.delete<void>(`/api/provider/my-sources/${encodeURIComponent(mihonProviderId)}`);
  },
};
