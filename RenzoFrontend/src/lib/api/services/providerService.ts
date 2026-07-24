import { apiClient } from '@/lib/api/client';
import type { Provider, ProviderPreferences } from '../types';

export const providerService = {
  /**
   * Gets a list of all available providers (installed and available to install)
   */
  async getProviders(): Promise<Provider[]> {
    return apiClient.get<Provider[]>('/api/provider/list');
  },

  /**
   * Installs a provider by package name
   */
  async installProvider(pkgName: string, options?: { repoName?: string; force?: boolean }): Promise<{ message: string }> {
    const params = new URLSearchParams();
    if (options?.repoName) params.append('repoName', options.repoName);
    if (options?.force !== undefined) params.append('force', String(options.force));
    const query = params.toString();
    return apiClient.post<{ message: string }>(`/api/provider/install/${pkgName}${query ? `?${query}` : ''}`, null);
  },

  /**
   * Installs a provider from an uploaded file
   */
  async installProviderFromFile(file: File, options?: { force?: boolean }): Promise<string> {
    const formData = new FormData();
    formData.append('file', file);

    const params = new URLSearchParams();
    if (options?.force !== undefined) params.append('force', String(options.force));
    const query = params.toString();

    return apiClient.post<string>(`/api/provider/install/file${query ? `?${query}` : ''}`, formData);
  },

  /**
   * Uninstalls a provider by package name (system-wide — affects every user).
   */
  async uninstallProvider(pkgName: string): Promise<{ message: string }> {
    return apiClient.post<{ message: string }>(`/api/provider/uninstall/${pkgName}`, null);
  },

  /**
   * "Install" for a source someone else already installed system-wide: adds
   * every source in that package to the current user's own enabled set. Cheap
   * — no APK re-fetch/recompile, unlike installProvider.
   */
  async enablePackageForMe(pkgName: string): Promise<void> {
    return apiClient.post<void>(`/api/provider/my-sources/package/${pkgName}`, {});
  },

  /**
   * Removes every source in a package from the current user's own enabled
   * set. Doesn't touch the shared install or any other user's access.
   */
  async disablePackageForMe(pkgName: string): Promise<void> {
    return apiClient.delete<void>(`/api/provider/my-sources/package/${pkgName}`);
  },

  /**
   * Gets provider preferences by package name
   */
  async getProviderPreferences(pkgName: string): Promise<ProviderPreferences> {
    return apiClient.get<ProviderPreferences>(`/api/provider/preferences/${pkgName}`);
  },

  /**
   * Sets provider preferences
   */
  async setProviderPreferences(preferences: ProviderPreferences): Promise<void> {
    return apiClient.post<void>('/api/provider/preferences', preferences);
  },
};
