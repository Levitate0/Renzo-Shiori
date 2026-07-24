import { apiClient } from '@/lib/api/client';

export interface ExtensionVersionInfo {
  version: string;
  isLocal: boolean;
  repositoryId: string;
}

export interface ExtensionInfo {
  name: string;
  autoUpdate: boolean;
  activeVersion: string;
  versions: ExtensionVersionInfo[];
}

/**
 * Extension version management: roll back to a known-good version, pin it
 * against auto-update while upstream ships a fix, or sideload a patched APK.
 */
export const extensionsService = {
  async list(): Promise<ExtensionInfo[]> {
    return apiClient.get<ExtensionInfo[]>('/api/extensions');
  },

  /** Switch active version; picking a non-latest version pins the extension. */
  async setActive(name: string, version: string): Promise<ExtensionInfo> {
    const p = new URLSearchParams({ name, version });
    return apiClient.post<ExtensionInfo>(`/api/extensions/active?${p.toString()}`, {});
  },

  /** Pin/unpin; unpinning re-activates the newest installed version. */
  async setAutoUpdate(name: string, enabled: boolean): Promise<ExtensionInfo> {
    const p = new URLSearchParams({ name, enabled: String(enabled) });
    return apiClient.post<ExtensionInfo>(`/api/extensions/autoupdate?${p.toString()}`, {});
  },

  /** Sideload an APK; compiled in a temp folder, swapped in only on success. */
  async sideload(file: File): Promise<ExtensionInfo> {
    const form = new FormData();
    form.append('file', file);
    return apiClient.post<ExtensionInfo>('/api/extensions/sideload', form);
  },
};
