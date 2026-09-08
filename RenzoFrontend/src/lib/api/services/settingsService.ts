import { apiClient } from '@/lib/api/client';
import { type Settings, type ContentPreferences } from '@/lib/api/types';

export interface SettingsUpdateResponse {
  message: string;
  setPasswordUrl?: string;
}

export const settingsService = {
  /**
   * The CALLING user's content preferences. Always fully populated — the server
   * fills anything the user hasn't set from the server defaults, so callers
   * never merge against the global settings themselves.
   */
  async getContentPreferences(): Promise<ContentPreferences> {
    return apiClient.get<ContentPreferences>('/api/settings/content-preferences');
  },

  async updateContentPreferences(prefs: ContentPreferences): Promise<ContentPreferences> {
    return apiClient.put<ContentPreferences>('/api/settings/content-preferences', prefs);
  },

  async getSettings(): Promise<Settings> {
    const data = await apiClient.get<Settings>('/api/settings');
    return {
      ...data,
    };
  },

  async getAvailableLanguages(): Promise<string[]> {
    return apiClient.get<string[]>('/api/settings/languages');
  },

  async updateSettings(settings: Settings): Promise<SettingsUpdateResponse> {
    const settingsPayload = {
      ...settings
    };

    return apiClient.put<SettingsUpdateResponse>('/api/settings', settingsPayload);
  },

  /** Sends a test email through the configured SMTP relay (owner only). */
  async sendTestEmail(to: string): Promise<{ success: boolean; message: string }> {
    return apiClient.post<{ success: boolean; message: string }>('/api/settings/test-email', { to });
  },
};
