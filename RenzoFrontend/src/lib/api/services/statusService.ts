import { apiClient } from '@/lib/api/client';
import type { SeriesHealth, ProviderHealth, StatusSummary, ClearAlertRequest } from '@/lib/api/types';

export const statusService = {
  /** Each user's health alerts are isolated to their own library; viewAll (Owner-level only) shows every user's. */
  async getSeriesStatus(viewAll = false): Promise<SeriesHealth[]> {
    return apiClient.get<SeriesHealth[]>(`/api/status/series${viewAll ? '?viewAll=true' : ''}`);
  },

  async getProviderStatus(viewAll = false): Promise<ProviderHealth[]> {
    return apiClient.get<ProviderHealth[]>(`/api/status/providers${viewAll ? '?viewAll=true' : ''}`);
  },

  async getStatusSummary(viewAll = false): Promise<StatusSummary> {
    return apiClient.get<StatusSummary>(`/api/status/summary${viewAll ? '?viewAll=true' : ''}`);
  },

  async clearAlert(request: ClearAlertRequest): Promise<void> {
    return apiClient.post<void>('/api/status/clear', request);
  },
};