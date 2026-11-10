import { apiClient } from '@/lib/api/client';
import { type SourceSpeed } from '@/lib/api/types';

/** How one source is doing across your series (server: Controllers/SourceHealthController.cs). */
export interface SourceHealth {
  provider: string;
  seriesCount: number;
  /** Series where the source is switched on. */
  enabledCount: number;
  /** Of those, the ones it is stepped down on right now. */
  steppedDownCount: number;
  reasons: string[];
  /** Reads and downloads that failed on it in the last day. */
  failuresLastDay: number;
  lastSuccessUtc: string | null;
  speed: SourceSpeed;
  status: 'dead' | 'failing' | 'ok' | 'idle';
}

/**
 * Sources › Health and the command bar's dead-source alert.
 * Hub twin: data/network/SourcesApi.kt (health calls).
 */
export const sourceHealthService = {
  /** Every source your series use, worst first. */
  async all(): Promise<SourceHealth[]> {
    return apiClient.get<SourceHealth[]>('/api/sources/health');
  },

  /** Only the sources that have failed everywhere for a day. */
  async alerts(): Promise<SourceHealth[]> {
    return apiClient.get<SourceHealth[]>('/api/sources/health/alerts');
  },
};
