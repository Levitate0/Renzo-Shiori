import { apiClient } from '@/lib/api/client';

export interface SiteInfo {
  provider: string;
  domain: string;
  supportsAutoLogin: boolean;
  /** True = source advertises a coin/paid gate; false = offered because you have series from it. */
  coin: boolean;
}

export interface SiteCredential {
  id: string;
  provider: string;
  username: string;
  /** ok | needs_login | failed | manual_cookie */
  status: string;
  statusDetail?: string | null;
  lastLoginAt?: string | null;
  supportsAutoLogin: boolean;
}

export interface SiteLoginResult {
  success: boolean;
  status: string;
  detail?: string | null;
  cookiesInjected: number;
}

interface SaveResponse {
  credential: SiteCredential;
  result: SiteLoginResult;
}

export const siteAuthService = {
  listSites(): Promise<SiteInfo[]> {
    return apiClient.get<SiteInfo[]>('/api/site-auth/sites');
  },
  list(): Promise<SiteCredential[]> {
    return apiClient.get<SiteCredential[]>('/api/site-auth');
  },
  save(provider: string, username: string, password: string): Promise<SaveResponse> {
    return apiClient.post<SaveResponse>('/api/site-auth', { provider, username, password });
  },
  saveCookie(provider: string, username: string, cookie: string): Promise<SaveResponse> {
    return apiClient.post<SaveResponse>('/api/site-auth/cookie', { provider, username, cookie });
  },
  relogin(id: string): Promise<SaveResponse> {
    return apiClient.post<SaveResponse>(`/api/site-auth/${id}/login`);
  },
  remove(id: string): Promise<void> {
    return apiClient.delete<void>(`/api/site-auth/${id}`);
  },
};
